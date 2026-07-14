using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EmuLibrary.Util.Vita
{
    // Reader + native decrypt/extract for PS Vita retail .pkg files (henkaku wiki "Packages"; psdevwiki
    // "PKG_files"). The container layout is the same PKG format PS3 uses (see Util/Ps3/Ps3Pkg.cs) — a plaintext
    // big-endian header, then an AES-128-CTR encrypted data segment holding the item table, item names, and file
    // data. Vita differs from PS3 in the AES key: instead of using a fixed key directly, the CTR key is DERIVED
    // by AES-ECB-encrypting the header IV (pkg_data_riv at 0x70) with one of three public "pkg_vita" keys,
    // selected by a key-type nibble in the header extension (byte 0xE7 & 7). The CTR counter is still
    // (iv + block-index), exactly like PS3.
    //
    // For a Vita APP/DLC/PATCH package the extraction also reproduces the on-console sce_sys/package/ metadata
    // that the runtime expects (head.bin, tail.bin, stat.bin, and body.bin renamed from the raw digs.bin) so the
    // decrypted tree is a complete drop-in. The per-title license (sce_sys/package/work.bin / the ux0 .rif) is
    // NOT written here — it is derived from the title's zRIF/work.bin by Util/Vita/Zrif.cs and written by the
    // install controller into ux0/license (see WS13 §9).
    //
    // Reference (permissively licensed, algorithm only — no code copied): mmozeiko's pkg2zip
    // (https://github.com/mmozeiko/pkg2zip, pkg2zip.c) and libpkg. The AES keys below are PUBLIC constants
    // published on the henkaku/psdevwiki (not secrets).
    //
    // [VERIFY] Crypto/extraction correctness is validated against real .pkg samples during the deferred manual
    // test — no sample files were available at implementation time. The unit tests cover the header/metadata
    // parse and the CTR math indirectly; the end-to-end decrypt of a real Vita package is the open item.
    internal sealed class VitaPkg : IDisposable
    {
        // Public "pkg_vita" AES-128 keys, indexed by key-type (2/3/4). Published on the henkaku wiki
        // (Packages#AES_Keys); not secrets. key-type 1 (PSP) uses a different scheme and is out of scope.
        private static readonly byte[] PkgVita2 =
        {
            0xE3, 0x1A, 0x70, 0xC9, 0xCE, 0x1D, 0xD7, 0x2B,
            0xF3, 0xC0, 0x62, 0x29, 0x63, 0xF2, 0xEC, 0xCB,
        };
        private static readonly byte[] PkgVita3 =
        {
            0x42, 0x3A, 0xCA, 0x3A, 0x2B, 0xD5, 0x64, 0x9F,
            0x96, 0x86, 0xAB, 0xAD, 0x6F, 0xD8, 0x80, 0x1F,
        };
        private static readonly byte[] PkgVita4 =
        {
            0xAF, 0x07, 0xFD, 0x59, 0x65, 0x25, 0x27, 0xBA,
            0xF1, 0x33, 0x89, 0x66, 0x8B, 0x17, 0xD9, 0xEA,
        };

        private const uint Magic = 0x7F504B47;    // "\x7FPKG"
        private const uint ExtMagic = 0x7F657874; // "\x7Fext" at offset 0xC0

        private sealed class PkgEntry
        {
            public string Name;
            public long NameOffset;  // relative to data segment start
            public long DataOffset;  // relative to data segment start
            public long DataSize;
            public byte Flags;       // low byte of the item flags word (offset 0x1B)
            public bool IsDirectory => Flags == 4 || Flags == 18;
        }

        private readonly FileStream _fs;
        private readonly long _fileSize;
        private readonly byte[] _iv;      // pkg_data_riv (header 0x70)
        private readonly byte[] _mainKey; // derived CTR key
        private byte[] _klicensee;        // inner PFS key (from the title's zRIF/work.bin); null = outer-only

        public long DataOffset { get; private set; } // enc_offset (header 0x20)
        public long DataSize { get; private set; }   // enc_size (header 0x28)
        public uint ItemCount { get; private set; }
        public long ItemsOffset { get; private set; } // item table offset within the data segment (metadata 13)
        public long ItemsSize { get; private set; }   // item table + names region size (metadata 13)
        public string ContentId { get; private set; }
        public string TitleId { get; private set; }
        public int KeyType { get; private set; }

        private VitaPkg(FileStream fs, long fileSize, byte[] iv, byte[] mainKey)
        {
            _fs = fs;
            _fileSize = fileSize;
            _iv = iv;
            _mainKey = mainKey;
        }

        // Opens and parses the header + metadata (no decryption of the body). Throws on bad magic / unsupported
        // key type. Caller must Dispose.
        //
        // When a klicensee is supplied (16 bytes, decoded from the title's zRIF/work.bin via Zrif.GetKlicensee),
        // ExtractTo / TryExtractItem additionally strip the inner PFS layer so PFS-resident files (eboot.bin,
        // sce_module/*, most sce_sys assets, Media/*) come out as plaintext — the drop-in shape Vita3K wants at
        // ux0/app. Without one, only the outer AES-128-CTR layer is removed (correct for NoNpDrm-style trees or
        // metadata-only reads).
        public static VitaPkg Open(string path, byte[] klicensee = null)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                long fileSize = fs.Length;

                // Header (0xC0) + ext header (0x40) + metadata all sit at the front of the file; read a generous
                // prefix so header, ext (key type at 0xE7) and the small metadata block parse in one shot.
                int prefixLen = (int)Math.Min(fileSize, 64 * 1024);
                var header = new byte[prefixLen];
                int read = ReadFull(fs, header, prefixLen);
                if (read < 0x100)
                    throw new InvalidDataException($"PKG \"{path}\" is too small to contain a Vita header.");

                if (ReadU32BE(header, 0x00) != Magic)
                    throw new InvalidDataException($"\"{path}\" is not a PKG (bad magic).");
                if (ReadU32BE(header, 0xC0) != ExtMagic)
                    throw new InvalidDataException($"\"{path}\" is not a Vita PKG (missing ext header).");

                long metaOffset = ReadU32BE(header, 0x08);
                uint metaCount = ReadU32BE(header, 0x0C);
                uint itemCount = ReadU32BE(header, 0x14);
                long encOffset = (long)ReadU64BE(header, 0x20);
                long encSize = (long)ReadU64BE(header, 0x28);
                var contentId = ReadFixedAscii(header, 0x30, 0x30);

                var iv = new byte[0x10];
                Buffer.BlockCopy(header, 0x70, iv, 0, 0x10);

                int keyType = header[0xE7] & 7;

                ParseMetadata(header, read, metaOffset, metaCount, out long itemsOffset, out long itemsSize);

                var mainKey = DeriveMainKey(keyType, iv);

                var pkg = new VitaPkg(fs, fileSize, iv, mainKey)
                {
                    DataOffset = encOffset,
                    DataSize = encSize,
                    ItemCount = itemCount,
                    ItemsOffset = itemsOffset,
                    ItemsSize = itemsSize,
                    ContentId = contentId,
                    TitleId = TitleIdFromContentId(contentId),
                    KeyType = keyType,
                };
                if (klicensee != null && klicensee.Length == 16)
                    pkg._klicensee = (byte[])klicensee.Clone();
                return pkg;
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        // Derives the AES-128-CTR key. For Vita (key-type 2/3/4) it is AES-ECB(pkg_vita_key, iv). Key-type 1
        // (PSP/PSX) and any other value are unsupported here (Vita-only RomType).
        private static byte[] DeriveMainKey(int keyType, byte[] iv)
        {
            byte[] vitaKey;
            switch (keyType)
            {
                case 2: vitaKey = PkgVita2; break;
                case 3: vitaKey = PkgVita3; break;
                case 4: vitaKey = PkgVita4; break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported Vita PKG key type {keyType}. Only PS Vita packages (key type 2/3/4) are supported.");
            }

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = vitaKey;
                using (var enc = aes.CreateEncryptor())
                {
                    var mainKey = new byte[16];
                    enc.TransformBlock(iv, 0, 16, mainKey, 0);
                    return mainKey;
                }
            }
        }

        // Walks the plaintext metadata records (8-byte {type,size} header + data), extracting the item-table
        // location (record type 13: items_offset, items_size). Best-effort within the read prefix.
        private static void ParseMetadata(byte[] buf, int available, long metaOffset, uint metaCount,
            out long itemsOffset, out long itemsSize)
        {
            itemsOffset = 0;
            itemsSize = 0;

            if (metaCount > 256)
                return; // implausible — treat as no metadata

            long o = metaOffset;
            for (int i = 0; i < metaCount; i++)
            {
                if (o + 8 > available)
                    break;
                uint type = ReadU32BE(buf, (int)o);
                int size = (int)ReadU32BE(buf, (int)o + 4);
                o += 8;
                if (o + size > available || size < 0)
                    break;
                if (type == 13 && size >= 8)
                {
                    itemsOffset = ReadU32BE(buf, (int)o);
                    itemsSize = ReadU32BE(buf, (int)o + 4);
                }
                o += size;
            }
        }

        // Decrypts + extracts every file entry under targetDir, plus the reconstructed sce_sys/package/ metadata
        // (head.bin, tail.bin, stat.bin) that the Vita runtime expects. Honors cancellation. When supplied,
        // progress reports the INCREMENTAL decrypted bytes written per chunk (for the install progress bar).
        public void ExtractTo(string targetDir, CancellationToken ct, IProgress<long> progress = null)
        {
            Directory.CreateDirectory(targetDir);
            var fullTarget = Path.GetFullPath(targetDir);

            bool sceSysPackageSeen = false;

            foreach (var e in ReadFileTable())
            {
                ct.ThrowIfCancellationRequested();

                // digs.bin is written as body.bin, RAW (undecrypted) — the runtime consumes it as-is. This
                // mirrors pkg2zip's Vita handling. [VERIFY] against a real package + Vita3K.
                bool decrypt = true;
                var name = e.Name;
                if (!e.IsDirectory
                    && string.Equals(name, "sce_sys/package/digs.bin", StringComparison.OrdinalIgnoreCase))
                {
                    name = "sce_sys/package/body.bin";
                    decrypt = false;
                }

                var safeName = name.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                var outPath = Path.GetFullPath(Path.Combine(targetDir, safeName));
                if (!outPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"PKG entry \"{e.Name}\" escapes the target directory.");

                if (e.IsDirectory)
                {
                    Directory.CreateDirectory(outPath);
                    if (string.Equals(name, "sce_sys/package", StringComparison.OrdinalIgnoreCase))
                        sceSysPackageSeen = true;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (decrypt)
                        DecryptRegionTo(e.DataOffset, e.DataSize, outStream, ct, progress);
                    else
                        CopyRawRegion(DataOffset + e.DataOffset, e.DataSize, outStream, ct, progress);
                }
            }

            WriteSceSysPackageExtras(targetDir, sceSysPackageSeen, ct);

            // Strip the inner PFS layer (in place) so PFS-resident files become plaintext. No-op when no
            // klicensee was supplied or the tree carries no sce_pfs databases (already-plaintext trees).
            if (_klicensee != null)
                VitaPfs.DecryptTree(targetDir, _klicensee, ct);

            // sce_pfs (unicv.db/files.db/pflist) is PFS *metadata* consumed only by DecryptTree above; a clean
            // install (and NoNpDrm drop-in trees) never carry it. Drop it so ux0/app/<id> matches the real
            // decrypted-app shape Vita3K expects.
            var scePfsDir = Path.Combine(targetDir, "sce_pfs");
            if (Directory.Exists(scePfsDir))
                Directory.Delete(scePfsDir, true);
        }

        // Extracts a single file entry's decrypted bytes by name (e.g. "sce_sys/icon0.png") without writing the
        // whole package — used for cheap scan-time icon retrieval. Returns false if the entry is absent or a
        // directory. Only the item table (near the file's front) plus the one entry's region are read.
        public bool TryExtractItem(string itemName, out byte[] data)
        {
            data = null;
            if (string.IsNullOrEmpty(itemName))
                return false;

            foreach (var e in ReadFileTable())
            {
                if (e.IsDirectory)
                    continue;
                if (!string.Equals(e.Name, itemName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (e.DataSize <= 0 || e.DataSize > int.MaxValue)
                    return false;
                data = DecryptRegion(e.DataOffset, (int)e.DataSize);

                // If this entry is a PFS-encrypted file, strip the inner layer too so callers (e.g. scan-time
                // icon extraction) get plaintext. Needs the sce_pfs databases, which are themselves outer-layer
                // files decrypted the same way.
                if (_klicensee != null
                    && TryExtractItemOuter("sce_pfs/unicv.db", out var unicvDb)
                    && TryExtractItemOuter("sce_pfs/files.db", out var filesDb)
                    && VitaPfs.TryDecryptItem(unicvDb, filesDb, e.Name, data, _klicensee, out var plaintext))
                {
                    data = plaintext;
                }
                return true;
            }
            return false;
        }

        // Outer-layer (CTR only) extraction of a single named entry — used to fetch the sce_pfs databases needed
        // to drive inner PFS decryption of another entry.
        private bool TryExtractItemOuter(string itemName, out byte[] data)
        {
            data = null;
            foreach (var e in ReadFileTable())
            {
                if (e.IsDirectory)
                    continue;
                if (!string.Equals(e.Name, itemName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (e.DataSize <= 0 || e.DataSize > int.MaxValue)
                    return false;
                data = DecryptRegion(e.DataOffset, (int)e.DataSize);
                return true;
            }
            return false;
        }

        // Reconstructs the sce_sys/package/ container files the Vita runtime expects alongside the decrypted
        // tree: head.bin (raw bytes [0, enc_offset + items_size)), tail.bin (raw bytes [enc_offset + enc_size,
        // file_end)), and a zeroed stat.bin (768 bytes). work.bin (the license) is written separately from the
        // title's zRIF (see Zrif). [VERIFY] exact set Vita3K requires.
        private void WriteSceSysPackageExtras(string targetDir, bool sceSysPackageSeen, CancellationToken ct)
        {
            var packageDir = Path.Combine(targetDir, "sce_sys", "package");
            Directory.CreateDirectory(packageDir);

            long headSize = DataOffset + ItemsSize;
            using (var head = new FileStream(Path.Combine(packageDir, "head.bin"), FileMode.Create, FileAccess.Write, FileShare.None))
                CopyRawRegion(0, headSize, head, ct, null);

            long tailStart = DataOffset + DataSize;
            long tailSize = _fileSize - tailStart;
            using (var tail = new FileStream(Path.Combine(packageDir, "tail.bin"), FileMode.Create, FileAccess.Write, FileShare.None))
                CopyRawRegion(tailStart, tailSize, tail, ct, null);

            File.WriteAllBytes(Path.Combine(packageDir, "stat.bin"), new byte[768]);
        }

        private System.Collections.Generic.List<PkgEntry> ReadFileTable()
        {
            if (ItemCount > 0x100000)
                throw new InvalidDataException($"PKG item count {ItemCount} is implausibly large.");

            var table = DecryptRegion(ItemsOffset, checked((int)(ItemCount * 0x20)));
            var entries = new System.Collections.Generic.List<PkgEntry>((int)ItemCount);

            for (int i = 0; i < ItemCount; i++)
            {
                int o = i * 0x20;
                uint nameOffset = ReadU32BE(table, o + 0x00);
                uint nameSize = ReadU32BE(table, o + 0x04);
                ulong dataOffset = ReadU64BE(table, o + 0x08);
                ulong dataSize = ReadU64BE(table, o + 0x10);
                byte flags = table[o + 0x1B];

                var nameBytes = DecryptRegion((long)nameOffset, (int)Math.Min(nameSize, 4096));
                var name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');

                entries.Add(new PkgEntry
                {
                    Name = name,
                    NameOffset = nameOffset,
                    DataOffset = (long)dataOffset,
                    DataSize = (long)dataSize,
                    Flags = flags,
                });
            }

            return entries;
        }

        // Decrypts [sectionOffset, sectionOffset+length) of the data segment into a new byte[].
        private byte[] DecryptRegion(long sectionOffset, int length)
        {
            using (var ms = new MemoryStream(length))
            {
                DecryptRegionTo(sectionOffset, length, ms, CancellationToken.None);
                return ms.ToArray();
            }
        }

        // Streams the decrypted [sectionOffset, sectionOffset+length) of the data segment into output. CTR: the
        // keystream block N = AES-ECB(mainKey, iv + N); N = sectionOffset/16, advancing per 16-byte block. Same
        // structure as Ps3Pkg, with the derived main key in place of the fixed gpkg key.
        private void DecryptRegionTo(long sectionOffset, long length, Stream output, CancellationToken ct, IProgress<long> progress = null)
        {
            if (length <= 0)
                return;

            ulong startBlock = (ulong)(sectionOffset / 16);
            int drop = (int)(sectionOffset % 16);
            long alignedAbs = DataOffset + (long)startBlock * 16;
            _fs.Seek(alignedAbs, SeekOrigin.Begin);

            const int maxChunkBytes = 0x10000 * 16; // 1 MiB worth of 16-byte blocks
            long alignedNeeded = ((drop + length + 15) / 16) * 16;
            int chunkBytes = (int)Math.Min(maxChunkBytes, alignedNeeded);
            var cipher = new byte[chunkBytes];
            var keystream = new byte[chunkBytes];

            long toOutput = length;
            ulong block = startBlock;

            using (var aes = CreateEcb())
            using (var enc = aes.CreateEncryptor())
            {
                while (toOutput > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    long wantDecrypted = drop + toOutput;
                    int thisChunk = (int)Math.Min(chunkBytes, wantDecrypted);
                    int blocks = (thisChunk + 15) / 16;
                    int readLen = blocks * 16;

                    int got = ReadFull(_fs, cipher, readLen);
                    if (got <= 0)
                        break;
                    int gotBlocks = (got + 15) / 16;

                    for (int b = 0; b < gotBlocks; b++)
                        WriteCounter(keystream, b * 16, _iv, block + (ulong)b);

                    enc.TransformBlock(keystream, 0, gotBlocks * 16, keystream, 0);

                    for (int i = 0; i < got; i++)
                        cipher[i] ^= keystream[i];

                    int avail = got - drop;
                    if (avail > 0)
                    {
                        int outLen = (int)Math.Min(avail, toOutput);
                        output.Write(cipher, drop, outLen);
                        toOutput -= outLen;
                        progress?.Report(outLen);
                    }

                    drop = 0;
                    block += (ulong)gotBlocks;
                }
            }
        }

        // Copies [absOffset, absOffset+length) of the file verbatim (no decryption) into output.
        private void CopyRawRegion(long absOffset, long length, Stream output, CancellationToken ct, IProgress<long> progress)
        {
            if (length <= 0)
                return;
            _fs.Seek(absOffset, SeekOrigin.Begin);
            var buffer = new byte[Math.Min(length, 1 << 20)];
            long remaining = length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = _fs.Read(buffer, 0, want);
                if (got <= 0)
                    break;
                output.Write(buffer, 0, got);
                remaining -= got;
                progress?.Report(got);
            }
        }

        private Aes CreateEcb()
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.KeySize = 128;
            aes.Key = _mainKey;
            return aes;
        }

        // Writes (iv + blockIndex), big-endian 128-bit counter, into dst at dstOff.
        private static void WriteCounter(byte[] dst, int dstOff, byte[] iv, ulong blockIndex)
        {
            Buffer.BlockCopy(iv, 0, dst, dstOff, 16);

            int idx = dstOff + 15;
            ulong add = blockIndex;
            uint carry = 0;
            while (idx >= dstOff && (add != 0 || carry != 0))
            {
                ulong val = (ulong)dst[idx] + (byte)(add & 0xFF) + carry;
                dst[idx] = (byte)val;
                carry = (uint)(val >> 8);
                add >>= 8;
                idx--;
            }
        }

        // Content-id form is "AAAAAA-TITLEID_NN-XXXXXXXXXXXXXXXX"; the title id is the chars after the first
        // '-' up to the '_' (same shape as PS3). Kept here so VitaPkg has no dependency on the Ps3 namespace.
        public static string TitleIdFromContentId(string contentId)
        {
            if (string.IsNullOrWhiteSpace(contentId))
                return null;

            int dash = contentId.IndexOf('-');
            if (dash < 0 || dash + 1 >= contentId.Length)
                return null;

            int underscore = contentId.IndexOf('_', dash + 1);
            int end = underscore < 0 ? contentId.Length : underscore;
            var titleId = contentId.Substring(dash + 1, end - (dash + 1));
            return string.IsNullOrWhiteSpace(titleId) ? null : titleId;
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n == 0)
                    break;
                total += n;
            }
            return total;
        }

        private static uint ReadU32BE(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        private static ulong ReadU64BE(byte[] b, int o) =>
            ((ulong)ReadU32BE(b, o) << 32) | ReadU32BE(b, o + 4);

        private static string ReadFixedAscii(byte[] b, int o, int len) =>
            Encoding.ASCII.GetString(b, o, len).TrimEnd('\0');

        public void Dispose()
        {
            _fs?.Dispose();
        }
    }
}
