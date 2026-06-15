using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EmuLibrary.Util.Ps3
{
    // Reader + native decrypt/extract for PS3 retail .pkg files (psdevwiki "PKG_files").
    //
    // The header is plaintext (all multi-byte fields BIG-ENDIAN). The data segment — file records,
    // file names and file data — is AES-128-CTR encrypted with the PUBLIC PS3 retail key and an
    // IV (pkg_data_riv) carried in the header. CTR keystream block N = AES-ECB(key, riv + N), so any
    // region of the segment can be decrypted independently by seeking the counter.
    //
    // We reimplement the documented algorithm (refs: pkgrip / mathieulh / psdevwiki); no GPL code is copied.
    // Debug / non-finalized pkgs use a different scheme and are out of scope (detected + rejected).
    internal sealed class Ps3Pkg : IDisposable
    {
        // Public PS3 retail PKG AES-128 key (the "gpkg" key). Not a secret — published on psdevwiki.
        private static readonly byte[] GpkgKey =
        {
            0x2E, 0x7B, 0x71, 0xD7, 0xC9, 0xC9, 0xA1, 0x4E,
            0xA3, 0x22, 0x1F, 0x18, 0x88, 0x28, 0xB8, 0xF8,
        };

        private const uint Magic = 0x7F504B47; // "\x7FPKG"

        private sealed class PkgEntry
        {
            public string Name;
            public long DataOffset; // relative to data segment start
            public long DataSize;
            public uint Flags;
            public bool IsDirectory => (Flags & 0xFF) == 4;
        }

        private readonly FileStream _fs;
        private readonly byte[] _riv;

        public ushort Revision { get; private set; }
        public ushort PkgType { get; private set; }
        public bool IsFinalizedRetail => (Revision & 0x8000) != 0;
        public long DataOffset { get; private set; }
        public long DataSize { get; private set; }
        public uint ItemCount { get; private set; }
        public string ContentId { get; private set; }
        public string TitleId { get; private set; }

        // From the plaintext metadata block (id 0x03, bit 0x10): set on update/patch packages. Verified a
        // perfect discriminator vs base/DLC across 400 real pkgs (disc + PSN + content). No decryption needed.
        public bool IsPatch { get; private set; }

        private Ps3Pkg(FileStream fs, byte[] riv)
        {
            _fs = fs;
            _riv = riv;
        }

        // Opens and parses the header. Throws on bad magic. Caller must Dispose.
        public static Ps3Pkg Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                // Header is 0x80; the plaintext metadata block follows it and is small. Read a generous
                // prefix so we can parse both in one shot.
                var header = new byte[8192];
                int read = ReadFull(fs, header, header.Length);
                if (read < 0x80)
                    throw new InvalidDataException($"PKG \"{path}\" is too small to contain a header.");

                if (ReadU32BE(header, 0x00) != Magic)
                    throw new InvalidDataException($"\"{path}\" is not a PKG (bad magic).");

                // pkg_data_riv (the AES-128-CTR IV) is at 0x70. 0x30 is a 0x30-byte content-id field, 0x60
                // is the QA digest, 0x70 is the riv (psdevwiki "PKG_files"). Reading 0x60 yields the digest
                // and produces garbage plaintext (file table, names, PARAM.SFO all unreadable).
                var riv = new byte[0x10];
                Buffer.BlockCopy(header, 0x70, riv, 0, 0x10);

                var pkg = new Ps3Pkg(fs, riv)
                {
                    Revision = ReadU16BE(header, 0x04),
                    PkgType = ReadU16BE(header, 0x06),
                    ItemCount = ReadU32BE(header, 0x14),
                    DataOffset = (long)ReadU64BE(header, 0x20),
                    DataSize = (long)ReadU64BE(header, 0x28),
                    ContentId = ReadFixedAscii(header, 0x30, 0x24),
                };
                pkg.TitleId = Ps3FileInfo.TitleIdFromContentId(pkg.ContentId);
                pkg.IsPatch = ReadPatchFlag(header, read, ReadU32BE(header, 0x08), ReadU32BE(header, 0x0C));
                return pkg;
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        // Walks the plaintext metadata block (id/size/data records). Returns true if the content-flags
        // entry (id 0x03) has the patch bit (0x10) set. Best-effort: false if the block isn't in-buffer.
        private static bool ReadPatchFlag(byte[] buf, int available, uint metaOffset, uint metaCount)
        {
            if (metaCount > 64)
                return false; // implausible — treat as no metadata

            int o = (int)metaOffset;
            for (int i = 0; i < metaCount; i++)
            {
                if (o + 8 > available)
                    break;
                uint id = ReadU32BE(buf, o);
                int size = (int)ReadU32BE(buf, o + 4);
                o += 8;
                if (o + size > available)
                    break;
                if (id == 0x03 && size >= 4)
                    return (ReadU32BE(buf, o) & 0x10) != 0;
                o += size;
            }
            return false;
        }

        // Reads + decrypts the embedded PARAM.SFO, if present. Returns null if absent/unparseable.
        public ParamSfo ReadParamSfo()
        {
            foreach (var e in ReadFileTable())
            {
                if (!e.IsDirectory && string.Equals(e.Name, "PARAM.SFO", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = DecryptRegion(e.DataOffset, (int)Math.Min(e.DataSize, 1 << 20));
                    return ParamSfo.TryParse(bytes, out var sfo) ? sfo : null;
                }
            }
            return null;
        }

        // Decrypts + extracts every file entry under targetDir. Honors cancellation.
        //
        // protectBootableRootParamSfo: base, updates and DLC all share the game's dev_hdd0/game/<id> dir and
        // each PKG carries its own root PARAM.SFO. RPCS3 reads that dir's root PARAM.SFO CATEGORY to decide
        // the title is launchable (HG = HDD game, DG = disc game; GD = "game data" / add-on is NOT bootable).
        // With last-writer-wins extraction, an add-on PKG (DLC, or an oddly-packaged update) whose SFO is GD
        // would replace the base game's HG/DG SFO and the title silently stops booting. Pass true when laying
        // add-on content over a base: an existing BOOTABLE root SFO is then only overwritten by another
        // bootable one (so HG->HG version-bumping updates still apply), never downgraded to a non-bootable one.
        public void ExtractTo(string targetDir, CancellationToken ct, bool protectBootableRootParamSfo = false)
        {
            if (!IsFinalizedRetail || PkgType != 0x0001)
                throw new NotSupportedException($"Unsupported PKG type (revision=0x{Revision:X4}, type=0x{PkgType:X4}). Only finalized retail PS3 packages are supported.");

            Directory.CreateDirectory(targetDir);

            // Read this package's own category once, only when we need to weigh a root-SFO overwrite.
            string incomingCategory = protectBootableRootParamSfo ? ReadParamSfo()?.Category : null;

            foreach (var e in ReadFileTable())
            {
                ct.ThrowIfCancellationRequested();

                // Guard against path traversal from a malformed/hostile name.
                var safeName = e.Name.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                var outPath = Path.GetFullPath(Path.Combine(targetDir, safeName));
                if (!outPath.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"PKG entry \"{e.Name}\" escapes the target directory.");

                if (e.IsDirectory)
                {
                    Directory.CreateDirectory(outPath);
                    continue;
                }

                if (protectBootableRootParamSfo
                    && string.Equals(safeName, "PARAM.SFO", StringComparison.OrdinalIgnoreCase)
                    && !IsBootableCategory(incomingCategory)
                    && File.Exists(outPath)
                    && ExistingRootSfoIsBootable(outPath))
                {
                    // Existing root SFO is a bootable game; this add-on's SFO isn't. Keep the bootable one.
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    DecryptRegionTo(e.DataOffset, e.DataSize, outStream, ct);
                }
            }
        }

        // RPCS3 boots a dev_hdd0/game/<id> entry only when its root PARAM.SFO CATEGORY is a game:
        // HG (HDD/PSN game) or DG (disc game). GD ("game data"), AC/DLC, etc. are not directly bootable.
        private static bool IsBootableCategory(string category) =>
            string.Equals(category, "HG", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "DG", StringComparison.OrdinalIgnoreCase);

        private static bool ExistingRootSfoIsBootable(string sfoPath)
        {
            try
            {
                return ParamSfo.TryParse(File.ReadAllBytes(sfoPath), out var sfo)
                    && sfo != null
                    && IsBootableCategory(sfo.Category);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // Relative install path + extracted size for every non-directory file this package writes into
        // dev_hdd0. PS3 PKGs are encrypted but not compressed, so DataSize is the exact on-disk size. Paths
        // are normalized the same way ExtractTo writes them, so a caller can union them across a title's
        // packages (updates/DLC overwrite same-path files, last writer wins) for an exact combined footprint.
        // The caller is expected to consume this transiently (per title) and persist only the summed result.
        public IEnumerable<KeyValuePair<string, long>> GetFileEntries()
        {
            foreach (var e in ReadFileTable())
            {
                if (e.IsDirectory)
                    continue;
                var name = e.Name.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                yield return new KeyValuePair<string, long>(name, e.DataSize);
            }
        }

        private List<PkgEntry> ReadFileTable()
        {
            if (ItemCount > 0x100000)
                throw new InvalidDataException($"PKG item count {ItemCount} is implausibly large.");

            var table = DecryptRegion(0, checked((int)(ItemCount * 0x20)));
            var entries = new List<PkgEntry>((int)ItemCount);

            // Names live in the same encrypted segment; decrypt each lazily.
            for (int i = 0; i < ItemCount; i++)
            {
                int o = i * 0x20;
                uint nameOffset = ReadU32BE(table, o + 0x00);
                uint nameSize = ReadU32BE(table, o + 0x04);
                ulong dataOffset = ReadU64BE(table, o + 0x08);
                ulong dataSize = ReadU64BE(table, o + 0x10);
                uint flags = ReadU32BE(table, o + 0x18);

                var nameBytes = DecryptRegion((long)nameOffset, (int)Math.Min(nameSize, 4096));
                var name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');

                entries.Add(new PkgEntry
                {
                    Name = name,
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

        // Streams the decrypted [sectionOffset, sectionOffset+length) of the data segment into output.
        private void DecryptRegionTo(long sectionOffset, long length, Stream output, CancellationToken ct)
        {
            if (length <= 0)
                return;

            ulong startBlock = (ulong)(sectionOffset / 16);
            int drop = (int)(sectionOffset % 16); // bytes to discard before the requested start
            long alignedAbs = DataOffset + (long)startBlock * 16;
            _fs.Seek(alignedAbs, SeekOrigin.Begin);

            // Size the working buffers to the region (capped at 1 MiB). Scan-time reads — the file table,
            // each file name, PARAM.SFO — are tiny; sizing to the region keeps them off the Large Object
            // Heap (>85 KiB) and avoids the LOH churn/fragmentation that exhausts memory when a pkg's file
            // table has thousands of name entries. Large extractions still stream in 1 MiB chunks.
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
                        WriteCounter(keystream, b * 16, _riv, block + (ulong)b);

                    enc.TransformBlock(keystream, 0, gotBlocks * 16, keystream, 0);

                    for (int i = 0; i < got; i++)
                        cipher[i] ^= keystream[i];

                    int avail = got - drop;
                    if (avail > 0)
                    {
                        int outLen = (int)Math.Min(avail, toOutput);
                        output.Write(cipher, drop, outLen);
                        toOutput -= outLen;
                    }

                    drop = 0;
                    block += (ulong)gotBlocks;
                }
            }
        }

        private static Aes CreateEcb()
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.KeySize = 128;
            aes.Key = GpkgKey;
            return aes;
        }

        // Writes (riv + blockIndex), big-endian 128-bit counter, into dst at dstOff.
        private static void WriteCounter(byte[] dst, int dstOff, byte[] riv, ulong blockIndex)
        {
            Buffer.BlockCopy(riv, 0, dst, dstOff, 16);

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

        private static ushort ReadU16BE(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

        private static uint ReadU32BE(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        private static ulong ReadU64BE(byte[] b, int o) =>
            ((ulong)ReadU32BE(b, o) << 32) | ReadU32BE(b, o + 4);

        private static string ReadFixedAscii(byte[] b, int o, int len)
        {
            return Encoding.ASCII.GetString(b, o, len).TrimEnd('\0');
        }

        public void Dispose()
        {
            _fs?.Dispose();
        }
    }
}
