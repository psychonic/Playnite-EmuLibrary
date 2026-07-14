using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EmuLibrary.Util.Vita
{
    // PS Vita PFS (PlayStation File System) inner-layer decryption for gamedata (PSN game / DLC) packages.
    //
    // A Vita game .pkg has TWO encryption layers. VitaPkg removes the outer AES-128-CTR layer; the files that
    // actually belong to the title's PFS image (eboot.bin, sce_module/*, Media/*, most sce_sys assets) are then
    // still encrypted with a per-title inner layer keyed off the license klicensee (from the zRIF / work.bin).
    // The plaintext drop-in that Vita3K wants at ux0/app/<TITLEID> is FULLY decrypted (Vita3K itself decrypts
    // the PFS at install time via psvpfsparser: vita3k/packages/src/pkg.cpp calls the same `execute()` step), so
    // EmuLibrary must reproduce that decryption when it drops files straight into ux0/app.
    //
    // Reference (permissively licensed, algorithm only — no code copied): motoharu-gosuto / Vita3K's
    // "psvpfsparser" (SecretGenerator, PfsKeyGenerator, PfsCryptEngine*, FlagOperations, UnicvDbTypes,
    // FilesDbParser, PfsPageMapper) and mmozeiko's pkg2zip (outer layer). The AES keys below are PUBLIC
    // constants published on the henkaku / psdevwiki (not secrets).
    //
    // Scope: only the gamedata / unicv.db path (image_spec 1/4, CRYPTO_ENGINE_CRYPTO_USE_KEYGEN — AES-CBC with a
    // per-sector tweak-as-IV, key = F00D(klicensee)) is implemented, which is what retail PSN game/DLC PKGs use.
    // Integrity-check (ICV / hash-tree) VERIFICATION is intentionally skipped — we only need plaintext, and the
    // ICV `secret` derivation (which the file<->table page mapping does use) is reproduced below in software
    // (the Vita's "F00D" hardware key service reduces, for PFS key_id 0, to a single public AES-ECB-decrypt).
    internal static class VitaPfs
    {
        // Derives the ICV `secret` for a given klicensee + salts (psvpfsparser generate_secret_np). Exposed for
        // unit testing against a known-good vector; also the primitive the bruteforce mapping relies on.
        internal static byte[] DeriveSecret(byte[] klicensee, uint filesSalt, uint icvSalt)
        {
            var realKey = F00DDeriveKey(klicensee);
            using (var aes = CreateAes(realKey))
            using (var e = aes.CreateEncryptor())
                return ComputeSecret(e, filesSalt, icvSalt);
        }

        // --- PUBLIC constants (psdevwiki / psvpfsparser) ---

        // F00D "kprx_auth_service_0x50001" reduces, for PFS (key_id 0), to AES-ECB-decrypt with this key.
        private static readonly byte[] ContractKey0 =
        {
            0xE1, 0x22, 0x13, 0xB4, 0x80, 0x16, 0xB0, 0xE9,
            0x9A, 0xB8, 0x1F, 0x8E, 0xC0, 0x2A, 0xD4, 0xA2,
        };
        // HMAC-SHA1 key used to derive the tweak-encryption key from the per-file dbseed (PfsKeys hmac_key0).
        private static readonly byte[] HmacKey0 =
        {
            0xE4, 0x62, 0x25, 0x8B, 0x1F, 0x31, 0x21, 0x56, 0x07, 0x45,
            0xDB, 0x62, 0xB1, 0x43, 0x67, 0x23, 0xD2, 0xBF, 0x80, 0xFE,
        };
        // HMAC-SHA1 key used to derive the ICV `secret` base from the salts (PfsKeys hmac_key1).
        private static readonly byte[] HmacKey1 =
        {
            0xAF, 0xE6, 0x56, 0xBB, 0x3C, 0x17, 0x25, 0x6A, 0x3C, 0x80,
            0x9F, 0x6E, 0x9B, 0xF1, 0x9F, 0xDD, 0x5A, 0x38, 0x85, 0x43,
        };
        // Fixed IV used when deriving the ICV `secret` (PfsKeys iv0).
        private static readonly byte[] Iv0 =
        {
            0x74, 0xD2, 0x0C, 0xC3, 0x98, 0x81, 0xC2, 0x13,
            0xEE, 0x77, 0x0B, 0x10, 0x10, 0xE4, 0xBE, 0xA7,
        };

        private const int PfsPageSize = 0x400;     // unicv.db / files.db page size
        private const int SectorSize = 0x8000;      // PFS file sector size (EXPECTED_FILE_SECTOR_SIZE)

        // A parsed unicv.db file table (one per PFS file/dir). Only the fields needed for decryption + the
        // file<->table page mapping are kept; the full signature list is not (ICV verification is skipped).
        private sealed class UnicvTable
        {
            public uint IcvSalt;      // page number of the first signature block (used as the ICV salt)
            public uint NumSectors;
            public byte[] Dbseed;     // 0x14, per-file seed for tweak-key derivation (version >= 2)
            public uint Version;
            public byte[] ZeroSig;    // 0x14, HMAC of the encrypted sector 0 (used to identify the file)
        }

        // Files.db file-type attribute bits (FlagOperations.h).
        private const ushort AttrDir = 0x8000;
        private const ushort AttrNenc = 0x4000;

        // Decrypts, in place, every PFS-encrypted file under an already outer-decrypted title tree at
        // <treeDir>. Files that are not part of the PFS (sce_pfs metadata, the reconstructed sce_sys/package
        // extras) and files marked "not encrypted" (e.g. param.sfo) are left untouched. Best-effort: if the
        // sce_pfs databases are absent (NoNpDrm-style already-plaintext trees) this is a no-op.
        public static void DecryptTree(string treeDir, byte[] klicensee, CancellationToken ct, IProgress<long> progress = null)
        {
            if (klicensee == null || klicensee.Length != 16)
                return;

            var unicvPath = Path.Combine(treeDir, "sce_pfs", "unicv.db");
            var filesDbPath = Path.Combine(treeDir, "sce_pfs", "files.db");
            if (!File.Exists(unicvPath) || !File.Exists(filesDbPath))
                return; // not a PFS tree (already plaintext) — nothing to do

            var tables = ParseUnicv(File.ReadAllBytes(unicvPath));
            var encryptedByKey = ParseFilesDbTypes(File.ReadAllBytes(filesDbPath), out uint filesSalt);

            var realKey = F00DDeriveKey(klicensee);

            // Enumerate candidate on-disk files (everything under the tree except the sce_pfs databases).
            var scePfsDir = Path.Combine(treeDir, "sce_pfs");
            var candidates = new List<string>();
            foreach (var f in Directory.EnumerateFiles(treeDir, "*", SearchOption.AllDirectories))
            {
                if (f.StartsWith(scePfsDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                candidates.Add(f);
            }

            // Pre-read the first sector of each candidate once (for signature matching).
            var firstSectors = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in candidates)
            {
                long len = new FileInfo(f).Length;
                if (len <= 0)
                    continue;
                int n = (int)Math.Min(len, SectorSize);
                var buf = new byte[n];
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ReadFull(fs, buf, n);
                firstSectors[f] = buf;
            }

            using (var aesEnc = CreateAes(realKey))
            using (var ecbEnc = aesEnc.CreateEncryptor())
            using (var ecbDec = aesEnc.CreateDecryptor())
            {
                foreach (var table in tables)
                {
                    ct.ThrowIfCancellationRequested();
                    if (table.NumSectors == 0 || table.ZeroSig == null)
                        continue;

                    // Identify the on-disk file for this table by matching the encrypted sector-0 HMAC.
                    var secret = ComputeSecret(ecbEnc, filesSalt, table.IcvSalt);
                    var signatureKey = HmacSha1(secret, LE32(0));

                    string matchPath = null;
                    foreach (var kv in firstSectors)
                    {
                        var sig = HmacSha1(signatureKey, kv.Value);
                        if (BytesEqual(sig, table.ZeroSig, 0x14))
                        {
                            matchPath = kv.Key;
                            break;
                        }
                    }
                    if (matchPath == null)
                        continue; // no matching file on disk (unexpected — leave as-is)

                    firstSectors.Remove(matchPath);

                    // Only decrypt files that files.db marks as encrypted (leave NENC files like param.sfo alone).
                    var name = Path.GetFileName(matchPath).ToLowerInvariant();
                    long size = new FileInfo(matchPath).Length;
                    if (!encryptedByKey.Contains(FileKey(name, size)))
                        continue;

                    var tweakEncKey = DeriveTweakEncKey(table);
                    DecryptFileInPlace(matchPath, tweakEncKey, ecbEnc, ecbDec, ct, progress);
                }
            }
        }

        // Decrypts a single already-outer-decrypted PFS file's bytes to plaintext, given the tree's unicv.db and
        // files.db (both plaintext, from the outer layer). Returns false if the file is not a PFS-encrypted file
        // (in which case `data` is already plaintext and unchanged). Used for cheap single-item extraction.
        public static bool TryDecryptItem(byte[] unicvDb, byte[] filesDb, string itemName, byte[] data, byte[] klicensee, out byte[] plaintext)
        {
            plaintext = data;
            if (klicensee == null || klicensee.Length != 16 || unicvDb == null || filesDb == null || data == null || data.Length == 0)
                return false;

            var encryptedByKey = ParseFilesDbTypes(filesDb, out uint filesSalt);
            var name = (itemName ?? string.Empty);
            int slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0)
                name = name.Substring(slash + 1);
            name = name.ToLowerInvariant();
            if (!encryptedByKey.Contains(FileKey(name, data.Length)))
                return false; // not encrypted (or unknown) — leave as-is

            var tables = ParseUnicv(unicvDb);
            var realKey = F00DDeriveKey(klicensee);

            int firstLen = Math.Min(data.Length, SectorSize);
            var firstSector = new byte[firstLen];
            Buffer.BlockCopy(data, 0, firstSector, 0, firstLen);

            using (var aes = CreateAes(realKey))
            using (var ecbEnc = aes.CreateEncryptor())
            using (var ecbDec = aes.CreateDecryptor())
            {
                foreach (var table in tables)
                {
                    if (table.NumSectors == 0 || table.ZeroSig == null)
                        continue;
                    var secret = ComputeSecret(ecbEnc, filesSalt, table.IcvSalt);
                    var signatureKey = HmacSha1(secret, LE32(0));
                    var sig = HmacSha1(signatureKey, firstSector);
                    if (!BytesEqual(sig, table.ZeroSig, 0x14))
                        continue;

                    var tweakEncKey = DeriveTweakEncKey(table);
                    var outBuf = (byte[])data.Clone();
                    DecryptBuffer(outBuf, tweakEncKey, ecbEnc, ecbDec);
                    plaintext = outBuf;
                    return true;
                }
            }
            return false;
        }

        // --- key derivation ---

        // F00D native key service for PFS (key_id 0): drv_key = AES-128-ECB-decrypt(klicensee) with ContractKey0.
        private static byte[] F00DDeriveKey(byte[] klicensee)
        {
            using (var aes = CreateAes(ContractKey0))
            using (var dec = aes.CreateDecryptor())
            {
                var outb = new byte[16];
                dec.TransformBlock(klicensee, 0, 16, outb, 0);
                return outb;
            }
        }

        // tweak_enc_key: for unicv version >= 2 (has dbseed) it is HMAC-SHA1(hmac_key0, dbseed)[0:16]; for
        // version 1 it would be derived from the salts, but retail PKGs are version 2 (asserted at parse time).
        private static byte[] DeriveTweakEncKey(UnicvTable table)
        {
            var digest = HmacSha1(HmacKey0, table.Dbseed);
            var key = new byte[16];
            Buffer.BlockCopy(digest, 0, key, 0, 16);
            return key;
        }

        // ICV `secret` (psvpfsparser generate_secret_np): combo = HMAC-SHA1(hmac_key1, filesSalt||icvSalt);
        // secret = AES-CBC-CTS-encrypt(realKey, iv0, combo[0x14]). realKey = F00D(klicensee) (ecbEnc holds it).
        private static byte[] ComputeSecret(ICryptoTransform ecbEnc, uint filesSalt, uint icvSalt)
        {
            byte[] combo;
            if (filesSalt == 0)
                combo = HmacSha1(HmacKey1, LE32(icvSalt));
            else
            {
                var msg = new byte[8];
                Buffer.BlockCopy(LE32(filesSalt), 0, msg, 0, 4);
                Buffer.BlockCopy(LE32(icvSalt), 0, msg, 4, 4);
                combo = HmacSha1(HmacKey1, msg);
            }

            // CBC-CTS over 0x14 bytes: one full 16-byte block, then a 4-byte tail.
            var secret = new byte[0x14];
            var block = new byte[16];
            for (int i = 0; i < 16; i++)
                block[i] = (byte)(combo[i] ^ Iv0[i]);
            var c0 = new byte[16];
            ecbEnc.TransformBlock(block, 0, 16, c0, 0); // C0 = ENC(realKey, combo[0:16] ^ iv0)
            Buffer.BlockCopy(c0, 0, secret, 0, 16);

            var tweakEnc = new byte[16];
            ecbEnc.TransformBlock(c0, 0, 16, tweakEnc, 0); // ENC(realKey, C0)
            for (int i = 0; i < 4; i++)
                secret[16 + i] = (byte)(combo[16 + i] ^ tweakEnc[i]);
            return secret;
        }

        // --- file decryption ---

        private static void DecryptFileInPlace(string path, byte[] tweakEncKey, ICryptoTransform ecbEnc, ICryptoTransform ecbDec, CancellationToken ct, IProgress<long> progress)
        {
            byte[] data = File.ReadAllBytes(path);
            DecryptBuffer(data, tweakEncKey, ecbEnc, ecbDec, ct, progress);
            File.WriteAllBytes(path, data);
        }

        // Round-trips the sector cipher for unit testing: decrypts `data` with the effective CBC key + tweak
        // key, without any DB parsing. (dataKey is the post-F00D CBC key; tweakEncKey the per-file tweak key.)
        internal static byte[] DecryptSectorsForTest(byte[] data, byte[] dataKey, byte[] tweakEncKey)
        {
            var buf = (byte[])data.Clone();
            using (var aes = CreateAes(dataKey))
            using (var e = aes.CreateEncryptor())
            using (var d = aes.CreateDecryptor())
                DecryptBuffer(buf, tweakEncKey, e, d);
            return buf;
        }

        // Decrypts a whole PFS file buffer in place, sector (0x8000) by sector. Each sector's CBC IV is the
        // little-endian byte offset (sector_index * sector_size) XOR tweak_enc_key. The final partial sector
        // uses ciphertext-stealing for a non-16-aligned tail (psvpfsparser AESCBCDecrypt_base).
        private static void DecryptBuffer(byte[] data, byte[] tweakEncKey, ICryptoTransform ecbEnc, ICryptoTransform ecbDec, CancellationToken ct = default(CancellationToken), IProgress<long> progress = null)
        {
            int offset = 0;
            long sector = 0;
            var iv = new byte[16];
            var tmp = new byte[16];
            var prev = new byte[16];
            while (offset < data.Length)
            {
                ct.ThrowIfCancellationRequested();
                int sectorLen = Math.Min(SectorSize, data.Length - offset);

                // IV = LE64(sector * SectorSize) (low 8 bytes) ^ tweak_enc_key
                long byteOffset = sector * SectorSize;
                for (int i = 0; i < 8; i++)
                    iv[i] = (byte)(byteOffset >> (8 * i));
                for (int i = 8; i < 16; i++)
                    iv[i] = 0;
                for (int i = 0; i < 16; i++)
                    iv[i] ^= tweakEncKey[i];

                int sizeBlock = sectorLen & ~0xF;
                int sizeTail = sectorLen & 0xF;

                // CBC decrypt of the aligned portion. prev starts at the IV.
                Buffer.BlockCopy(iv, 0, prev, 0, 16);
                for (int b = 0; b < sizeBlock; b += 16)
                {
                    // t = ECB_dec(realKey, C); P = t ^ prev; prev = C
                    ecbDec.TransformBlock(data, offset + b, 16, tmp, 0);
                    for (int i = 0; i < 16; i++)
                    {
                        byte cipher = data[offset + b + i];
                        data[offset + b + i] = (byte)(tmp[i] ^ prev[i]);
                        prev[i] = cipher;
                    }
                }

                // Ciphertext-stealing tail: tail = C_tail ^ ECB_enc(realKey, last-ciphertext-block).
                if (sizeTail != 0)
                {
                    var tweakEnc = new byte[16];
                    ecbEnc.TransformBlock(prev, 0, 16, tweakEnc, 0);
                    for (int i = 0; i < sizeTail; i++)
                        data[offset + sizeBlock + i] ^= tweakEnc[i];
                }

                progress?.Report(sectorLen);
                offset += sectorLen;
                sector++;
            }
        }

        // --- unicv.db parsing ---

        // Parses unicv.db (SCEIRODB) into the list of file tables. Each non-blank SCEIFTBL page starts a table;
        // its ICV salt is the page index of the following signature block, whose first 0x14-byte signature is
        // the encrypted-sector-0 HMAC used to identify the on-disk file.
        private static List<UnicvTable> ParseUnicv(byte[] db)
        {
            var tables = new List<UnicvTable>();
            if (db.Length < PfsPageSize || Encoding.ASCII.GetString(db, 0, 8) != "SCEIRODB")
                return tables;

            int nPages = db.Length / PfsPageSize;
            for (int p = 0; p < nPages; p++)
            {
                int off = p * PfsPageSize;
                if (!MagicEquals(db, off, "SCEIFTBL"))
                    continue;

                uint version = U32(db, off + 8);
                uint nSectors = U32(db, off + 20);
                // sce_iftbl_header_t: magic[8] ver[4] pageSize[4] binTreeNumMaxAvail[4] nSectors[4]
                //                     fileSectorSize[4] padding[4] data1[20] dbseed[20]
                var dbseed = new byte[0x14];
                Buffer.BlockCopy(db, off + 52, dbseed, 0, 0x14);

                var table = new UnicvTable
                {
                    // The ICV salt is the page index of the SCEIFTBL header itself (verified empirically against
                    // the reference's page<->file mapping); the signature block sits on the following page.
                    IcvSalt = (uint)p,
                    NumSectors = nSectors,
                    Version = version,
                    Dbseed = dbseed,
                };

                if (nSectors > 0)
                {
                    int sigPage = off + PfsPageSize;      // first sig_tbl_t block
                    if (sigPage + 16 + 0x14 <= db.Length)
                    {
                        var zero = new byte[0x14];
                        Buffer.BlockCopy(db, sigPage + 16, zero, 0, 0x14); // sig_tbl_header_t is 16 bytes
                        table.ZeroSig = zero;
                    }
                }

                tables.Add(table);
            }
            return tables;
        }

        // --- files.db parsing (types only) ---

        // Parses files.db (SCENGPFS) and returns the set of (filename, size) keys that are PFS-ENCRYPTED files.
        // Only the flat per-block file records are read (name + type + size); full path reconstruction is not
        // needed because on-disk files are matched to tables by content, and (name, size) uniquely selects the
        // type in practice. Directories (ATTR_DIR) and "not-encrypted" (ATTR_NENC) files are excluded.
        private static HashSet<string> ParseFilesDbTypes(byte[] db, out uint filesSalt)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            filesSalt = 0;
            if (db.Length < PfsPageSize || Encoding.ASCII.GetString(db, 0, 8) != "SCENGPFS")
                return result;

            filesSalt = U32(db, 28);
            int pageSize = (int)U32(db, 16);
            if (pageSize <= 0)
                pageSize = PfsPageSize;
            int nPages = db.Length / pageSize;

            for (int p = 1; p < nPages; p++)
            {
                int off = p * pageSize;
                if (IsBlank(db, off, pageSize))
                    continue;

                // sce_ng_pfs_block_header_t: parent[4] type[4] nFiles[4] padding[4]
                uint nFiles = U32(db, off + 8);
                if (nFiles > 9)
                    continue;

                int filesOff = off + 16;               // 9 * sce_ng_pfs_file_header_t (72 bytes each)
                int infosOff = filesOff + 72 * 9;      // 10 * sce_ng_pfs_file_info_t (16 bytes each)
                for (int f = 0; f < nFiles; f++)
                {
                    int fh = filesOff + f * 72;
                    // sce_ng_pfs_file_header_t: index[4] fileName[68]
                    string fname = ReadCString(db, fh + 4, 68);
                    int inf = infosOff + f * 16;
                    // sce_ng_pfs_file_info_t: idx[4] type[2] padding0[2] size[4] padding1[4]
                    ushort type = U16(db, inf + 4);
                    uint fsize = U32(db, inf + 8);

                    bool isDir = (type & AttrDir) != 0;
                    bool isUnexisting = type == 0x0000;
                    bool isNenc = (type & AttrNenc) != 0;
                    if (isDir || isUnexisting || isNenc)
                        continue; // not an encrypted file
                    if (string.IsNullOrEmpty(fname) || fsize == 0)
                        continue;

                    result.Add(FileKey(fname.ToLowerInvariant(), fsize));
                }
            }
            return result;
        }

        // --- small helpers ---

        private static string FileKey(string lowerName, long size) => lowerName + "\0" + size.ToString();

        private static Aes CreateAes(byte[] key)
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.KeySize = 128;
            aes.Key = key;
            return aes;
        }

        private static byte[] HmacSha1(byte[] key, byte[] message)
        {
            using (var h = new HMACSHA1(key))
                return h.ComputeHash(message);
        }

        private static byte[] LE32(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

        private static bool MagicEquals(byte[] b, int off, string magic)
        {
            if (off + magic.Length > b.Length)
                return false;
            for (int i = 0; i < magic.Length; i++)
                if (b[off + i] != (byte)magic[i])
                    return false;
            return true;
        }

        private static bool BytesEqual(byte[] a, byte[] b, int n)
        {
            for (int i = 0; i < n; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static bool IsBlank(byte[] b, int off, int len)
        {
            for (int i = 0; i < len; i++)
                if (b[off + i] != 0)
                    return false;
            return true;
        }

        private static string ReadCString(byte[] b, int off, int max)
        {
            int end = off;
            int limit = Math.Min(off + max, b.Length);
            while (end < limit && b[end] != 0)
                end++;
            return Encoding.ASCII.GetString(b, off, end - off);
        }

        private static uint U32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

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
    }
}
