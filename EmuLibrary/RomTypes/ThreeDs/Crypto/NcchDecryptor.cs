using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using EmuLibrary.Util;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // Fully decrypts an NCCH (.app) in place to a "no_crypto" NCCH — the form Azahar's loader requires
    // (NCCHContainer::Load rejects any NCCH whose no_crypto flag is clear; Azahar never decrypts NCCH-internal
    // crypto at load time). Reproduces the GUI's NCCHCryptoFile::Write: AES-128-CTR decrypt of the exheader,
    // ExeFS and RomFS using the per-NCCH keys (KeyX by crypto method + KeyY from the header signature, with
    // optional seed crypto), then set the no_crypto flag.
    //
    // Input is the titlekey-CBC-stripped content (an encrypted NCCH); output is the launchable .app. Content
    // that is not an NCCH (e.g. DS-cart CFA with a different magic) or already no_crypto is left untouched.
    internal static class NcchDecryptor
    {
        private const int HeaderSize = 0x200;
        private const int MagicOffset = 0x100;
        private const int ProgramIdOffset = 0x118;
        private const int ExHeaderSizeOffset = 0x180;
        private const int FlagsOffset = 0x188;
        private const int ExeFsOffsetOffset = 0x1A0;
        private const int ExeFsSizeOffset = 0x1A4;
        private const int RomFsOffsetOffset = 0x1B0;
        private const int RomFsSizeOffset = 0x1B4;

        // NCCH flag byte 7 bit masks.
        private const byte FixedKeyFlag = 0x01;
        private const byte NoCryptoFlag = 0x04;
        private const byte SeedCryptoFlag = 0x20;

        // CTR section type bytes.
        private const byte SectionExHeader = 1;
        private const byte SectionExeFs = 2;
        private const byte SectionRomFs = 3;

        private const int RegionChunkSize = 1 << 20; // 1 MiB

        public static void DecryptFile(string path, ThreeDsKeys keys, CancellationToken ct, IProgress<long> progress = null)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Decrypt(fs, keys, ct, 0, progress);
        }

        // True if the NCCH at `ncchBase` is an encrypted NCCH (magic present, no_crypto clear). Used to decide
        // whether a loose file needs decrypting; the NCCH/NCSD headers are plaintext in encrypted dumps too.
        public static bool IsEncryptedNcch(FileStream fs, long ncchBase)
        {
            if (fs.Length < ncchBase + HeaderSize)
                return false;
            var header = new byte[HeaderSize];
            fs.Seek(ncchBase, SeekOrigin.Begin);
            ReadExactlyInto(fs, header, HeaderSize);
            if (Encoding.ASCII.GetString(header, MagicOffset, 4) != "NCCH")
                return false;
            return (header[FlagsOffset + 7] & NoCryptoFlag) == 0;
        }

        // When supplied, progress reports the INCREMENTAL plaintext bytes processed per chunk across the
        // exheader/ExeFS/RomFS regions (for the install progress bar). The regions sum to ≤ the content size.
        public static void Decrypt(FileStream fs, ThreeDsKeys keys, CancellationToken ct, long ncchBase = 0,
            IProgress<long> progress = null)
        {
            if (fs.Length < ncchBase + HeaderSize)
                return; // not an NCCH; leave as-is

            var header = new byte[HeaderSize];
            fs.Seek(ncchBase, SeekOrigin.Begin);
            ReadExactlyInto(fs, header, HeaderSize);

            if (Encoding.ASCII.GetString(header, MagicOffset, 4) != "NCCH")
                return; // DS-cart / non-NCCH content: stored verbatim

            byte cryptoMethod = header[FlagsOffset + 3];
            byte flags7 = header[FlagsOffset + 7];
            if ((flags7 & NoCryptoFlag) != 0)
                return; // already decrypted

            bool fixedKey = (flags7 & FixedKeyFlag) != 0;
            bool seedCrypto = (flags7 & SeedCryptoFlag) != 0;
            int mediaUnit = 0x200 << header[FlagsOffset + 6];

            byte[] primaryKey;
            byte[] secondaryKey;
            if (fixedKey)
            {
                // System/dev fixed-key titles use a zero normal key for both slots.
                primaryKey = new byte[16];
                secondaryKey = new byte[16];
            }
            else
            {
                var keyY = new byte[16];
                Buffer.BlockCopy(header, 0, keyY, 0, 16); // first 16 bytes of the NCCH signature

                var gen = keys.GeneratorConstant;
                primaryKey = ThreeDsKeyScrambler.Derive(keys.GetNcchKeyX(0x00), keyY, gen);

                var secondaryKeyY = keyY;
                if (seedCrypto)
                    secondaryKeyY = DeriveSeedKeyY(keyY, Le.U64(header, ProgramIdOffset), keys);

                secondaryKey = ThreeDsKeyScrambler.Derive(keys.GetNcchKeyX(cryptoMethod), secondaryKeyY, gen);
            }

            ulong partitionId = Le.U64(header, 0x108);

            using (var primary = new AesCtr(primaryKey))
            using (var secondary = new AesCtr(secondaryKey))
            {
                // ExHeader (+ AccessDescriptor): 0x800 bytes at 0x200, primary key, section type 1.
                uint exHeaderSize = Le.U32(header, ExHeaderSizeOffset);
                if (exHeaderSize > 0)
                    DecryptRegion(fs, primary, SectionCtr(partitionId, SectionExHeader), ncchBase + 0x200, 0x800, ct, progress);

                // ExeFS: header + icon/banner with primary key, the rest (.code) with secondary key.
                uint exefsOffMu = Le.U32(header, ExeFsOffsetOffset);
                uint exefsSizeMu = Le.U32(header, ExeFsSizeOffset);
                if (exefsSizeMu > 0)
                    DecryptExeFs(fs, primary, secondary, partitionId, ncchBase + (long)exefsOffMu * mediaUnit, (long)exefsSizeMu * mediaUnit, ct, progress);

                // RomFS: secondary key, section type 3.
                uint romfsOffMu = Le.U32(header, RomFsOffsetOffset);
                uint romfsSizeMu = Le.U32(header, RomFsSizeOffset);
                if (romfsSizeMu > 0)
                    DecryptRegion(fs, secondary, SectionCtr(partitionId, SectionRomFs), ncchBase + (long)romfsOffMu * mediaUnit, (long)romfsSizeMu * mediaUnit, ct, progress);
            }

            // Mark the partition fully decrypted: set no_crypto, clear fixed-key / seed bits and the method byte.
            header[FlagsOffset + 3] = 0;
            header[FlagsOffset + 7] = (byte)((flags7 & ~(FixedKeyFlag | SeedCryptoFlag)) | NoCryptoFlag);
            fs.Seek(ncchBase + FlagsOffset, SeekOrigin.Begin);
            fs.Write(header, FlagsOffset, 8);
            fs.Flush();
        }

        // The ExeFS header (first 0x200) lists up to 10 files (name[8], offset(u32 LE), size(u32 LE)). Only the
        // ".code" section uses the secondary key; the header and every other section (icon, banner, logo) use
        // the primary key. All offsets are relative to the start of the ExeFS, after its 0x200 header.
        private static void DecryptExeFs(FileStream fs, AesCtr primary, AesCtr secondary, ulong partitionId, long exefsOffset, long exefsSize, CancellationToken ct, IProgress<long> progress = null)
        {
            // Decrypt the ExeFS header itself (primary key) so we can read the file table, then re-read it.
            DecryptRegion(fs, primary, SectionCtr(partitionId, SectionExeFs), exefsOffset, 0x200, ct, progress);

            var exefsHeader = new byte[0x200];
            fs.Seek(exefsOffset, SeekOrigin.Begin);
            ReadExactlyInto(fs, exefsHeader, 0x200);

            for (int i = 0; i < 10; i++)
            {
                int o = i * 0x10;
                var name = Encoding.ASCII.GetString(exefsHeader, o, 8).TrimEnd('\0');
                uint fileOffset = Le.U32(exefsHeader, o + 0x08);
                uint fileSize = Le.U32(exefsHeader, o + 0x0C);
                if (fileSize == 0 && name.Length == 0)
                    continue;

                long regionOffset = 0x200 + fileOffset; // within the ExeFS
                var cipher = (name == ".code") ? secondary : primary;
                var ctr = SectionCtr(partitionId, SectionExeFs);
                AesCtr.Add(ctr, (ulong)(regionOffset / 16));
                DecryptRegion(fs, cipher, ctr, exefsOffset + regionOffset, fileSize, ct, progress);
            }
        }

        // Decrypts [offset, length) of the file in place with the running counter, in chunks (so multi-GB
        // RomFS never loads fully into memory). The counter is advanced across chunks by AesCtr.Apply.
        private static void DecryptRegion(FileStream fs, AesCtr cipher, byte[] counter, long offset, long length, CancellationToken ct, IProgress<long> progress = null)
        {
            if (length <= 0)
                return;

            var buffer = new byte[RegionChunkSize];
            long remaining = length;
            long pos = offset;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(RegionChunkSize, remaining);

                fs.Seek(pos, SeekOrigin.Begin);
                int read = ReadUpTo(fs, buffer, want);
                if (read == 0)
                    break;

                // CTR keystream is generated per 16-byte block; round the processed span up to a block so the
                // counter advances correctly for the next chunk. The trailing bytes past `read` are scratch.
                int blockLen = (read + 15) / 16 * 16;
                if (blockLen > buffer.Length)
                    blockLen = read; // can't happen (chunk size is block-aligned), defensive
                cipher.Apply(counter, buffer, 0, blockLen);

                fs.Seek(pos, SeekOrigin.Begin);
                fs.Write(buffer, 0, read);

                progress?.Report(read);
                pos += read;
                remaining -= read;
                if (read < want)
                    break;
            }
        }

        // Section CTR for NCCH version 2/0 (the retail case): partition id big-endian, then the section type
        // byte, then zeros. (Version-1 offset-counter NCCHs are not produced for CIA/CDN retail titles.)
        // Internal so the read-only SMDH reader can reproduce the ExeFS counter without duplicating it.
        internal static byte[] SectionCtr(ulong partitionId, byte section)
        {
            var ctr = new byte[16];
            for (int i = 0; i < 8; i++)
                ctr[i] = (byte)(partitionId >> (56 - i * 8));
            ctr[8] = section;
            return ctr;
        }

        private static byte[] DeriveSeedKeyY(byte[] keyY, ulong programId, ThreeDsKeys keys)
        {
            if (!keys.TryGetSeed(programId, out var seed))
                throw new Exception(
                    $"This title uses seed crypto but no seed for program id {programId:X16} was found. " +
                    "Provide a seeddb.bin beside aes_keys.txt.");

            var input = new byte[32];
            Buffer.BlockCopy(keyY, 0, input, 0, 16);
            Buffer.BlockCopy(seed, 0, input, 16, 16);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(input);
                var result = new byte[16];
                Buffer.BlockCopy(hash, 0, result, 0, 16);
                return result;
            }
        }

        private static int ReadUpTo(Stream s, byte[] buffer, int count) => StreamUtils.ReadUpTo(s, buffer, count);

        private static void ReadExactlyInto(Stream s, byte[] buffer, int count) => StreamUtils.ReadExactly(s, buffer, count, "NCCH stream");
    }
}
