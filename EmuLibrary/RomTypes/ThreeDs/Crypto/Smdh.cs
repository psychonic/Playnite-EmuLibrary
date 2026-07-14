using System;
using System.IO;
using System.Text;
using EmuLibrary.Util;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // Reads the SMDH ("icon") metadata embedded in a 3DS title: the per-language application title and the
    // 48x48 icon. The SMDH is the "icon" file inside the main NCCH's ExeFS; for an SD-installed title the boot
    // .app is a fully decrypted (no_crypto) NCCH, so the ExeFS is plaintext and no keys are needed to read it.
    //
    // SMDH layout (0x36C0 bytes): magic "SMDH" (0x00), 16 application-title structs of 0x200 each starting at
    // 0x08 (short desc [0x80] + long desc [0x100] + publisher [0x80], all UTF-16LE), then icon data at 0x2040
    // (small 24x24 at 0x2040, large 48x48 RGB565 at 0x24C0). Titles are ordered by language; English is index 1.
    internal static class Smdh
    {
        public const int Size = 0x36C0;
        private const int TitlesOffset = 0x08;
        private const int TitleStructSize = 0x200;
        private const int ShortDescSize = 0x80;
        private const int LargeIconOffset = 0x24C0;
        private const int IconDim = 48;

        // NCCH header field offsets shared by both read paths.
        private const int NcchHeaderSize = 0x200;
        private const int NcchMagicOffset = 0x100;
        private const int NcchFlagsOffset = 0x188;
        private const int NcchExeFsOffsetOffset = 0x1A0;
        private const int NcchPartitionIdOffset = 0x108;
        private const byte NoCryptoFlag = 0x04;
        private const byte FixedKeyFlag = 0x01;
        private const byte SectionExeFs = 2;

        // Language indices into the 16 title structs (English preferred, then a sensible Latin fallback chain).
        private static readonly int[] TitlePreference = { 1, 4, 3, 2, 5, 9, 8, 0, 10, 6, 11, 7 };

        public sealed class Info
        {
            public string Title;       // null if no usable title was present
            public byte[] IconBmp;     // 24-bit BMP bytes, null if no icon was present
        }

        // Parses an SMDH blob. Returns null only if the blob isn't a valid SMDH.
        public static Info Parse(byte[] smdh)
        {
            if (smdh == null || smdh.Length < Size)
                return null;
            if (Encoding.ASCII.GetString(smdh, 0, 4) != "SMDH")
                return null;

            return new Info
            {
                Title = ReadTitle(smdh),
                IconBmp = ReadLargeIconBmp(smdh),
            };
        }

        // Reads the SMDH "icon" file from a fully decrypted (no_crypto) NCCH .app. Returns null if the file is
        // not a no_crypto NCCH, has no ExeFS "icon" section, or can't be parsed.
        public static Info TryReadFromNcch(string appPath)
        {
            try
            {
                using (var fs = new FileStream(appPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var smdh = ReadIconExeFsEntry(fs);
                    return smdh == null ? null : Parse(smdh);
                }
            }
            catch
            {
                return null;
            }
        }

        // Reads a title's SMDH without decrypting the whole content. For an uninstalled (still-encrypted) CIA or
        // CDN title: strip the titlekey CBC layer off just the NCCH header / ExeFS header / "icon" region (random
        // access), and where the NCCH is itself encrypted, undo the NCCH crypto on those small regions with the
        // primary key (icon/banner use the primary key, so seed crypto never applies). Returns null on any
        // failure (missing keys/ticket, not an NCCH, no icon). Cheap even for multi-GB titles.
        public static Info TryReadFromEncryptedTitle(IDecryptableTitle title, ThreeDsKeys keys)
        {
            try
            {
                // The boot content (index 0) carries the application's main NCCH.
                ThreeDsTmdContent boot = null;
                foreach (var c in title.PresentContents)
                {
                    if (c.Index == 0) { boot = c; break; }
                }
                if (boot == null)
                    return null;

                var titleKey = title.Ticket.DecryptTitleKey(keys);

                // The NCCH begins at the start of the boot content, so absolute offsets map straight to the
                // titlekey-CBC-stripped content read (ncchBase = 0).
                return TryReadNcchIcon((offset, length) => title.ReadDecryptedRange(boot, titleKey, offset, length), 0, keys);
            }
            catch
            {
                return null;
            }
        }

        // Reads a title's SMDH from an NCCH whose bytes are exposed by `readAt(absoluteOffset, length)`, with the
        // NCCH itself starting at `ncchBase` within that address space. Used by both the encrypted-title path
        // (bytes come from a titlekey-CBC-stripped content read, ncchBase = 0) and the loose-file path (bytes come
        // straight from the file / a Z3DS prefix, ncchBase = the NCSD partition-0 offset or 0 for a bare NCCH).
        // Undoes the NCCH primary-key CTR over just the small ExeFS header / "icon" region when the NCCH is
        // encrypted (icon/banner always use the primary key, so seed crypto never applies). Returns null on any
        // failure (not an NCCH, no ExeFS icon, encrypted but no keys). Cheap even for multi-GB titles.
        public static Info TryReadNcchIcon(Func<long, int, byte[]> readAt, long ncchBase, ThreeDsKeys keys)
        {
            try
            {
                var header = readAt(ncchBase, NcchHeaderSize);
                if (header.Length < NcchHeaderSize || Encoding.ASCII.GetString(header, NcchMagicOffset, 4) != "NCCH")
                    return null;

                byte flags7 = header[NcchFlagsOffset + 7];
                bool noCrypto = (flags7 & NoCryptoFlag) != 0;
                bool fixedKey = (flags7 & FixedKeyFlag) != 0;
                int mediaUnit = 0x200 << header[NcchFlagsOffset + 6];
                long exefsOffset = (long)Le.U32(header, NcchExeFsOffsetOffset) * mediaUnit;
                long exefsSize = (long)Le.U32(header, NcchExeFsOffsetOffset + 4) * mediaUnit;
                if (exefsOffset <= 0 || exefsSize <= 0)
                    return null;

                AesCtr primary = null;
                try
                {
                    if (!noCrypto)
                    {
                        if (keys == null)
                            return null; // encrypted NCCH but no keys to undo it
                        byte[] primaryKey;
                        if (fixedKey)
                        {
                            primaryKey = new byte[16];
                        }
                        else
                        {
                            var keyY = new byte[16];
                            Buffer.BlockCopy(header, 0, keyY, 0, 16); // first 16 bytes of the NCCH signature
                            primaryKey = ThreeDsKeyScrambler.Derive(keys.GetNcchKeyX(0x00), keyY, keys.GeneratorConstant);
                        }
                        primary = new AesCtr(primaryKey);
                    }

                    ulong partitionId = Le.U64(header, NcchPartitionIdOffset);

                    var exefsHeader = readAt(ncchBase + exefsOffset, 0x200);
                    if (exefsHeader.Length < 0x200)
                        return null;
                    primary?.Apply(NcchDecryptor.SectionCtr(partitionId, SectionExeFs), exefsHeader, 0, 0x200);

                    if (!FindIconEntry(exefsHeader, out uint iconOffset, out uint iconSize) || iconSize < Size)
                        return null;

                    long iconRegionOffset = 0x200 + iconOffset; // within the ExeFS
                    var smdh = readAt(ncchBase + exefsOffset + iconRegionOffset, (int)iconSize);
                    if (smdh.Length < Size)
                        return null;
                    if (primary != null)
                    {
                        var ctr = NcchDecryptor.SectionCtr(partitionId, SectionExeFs);
                        AesCtr.Add(ctr, (ulong)(iconRegionOffset / 16));
                        primary.Apply(ctr, smdh, 0, smdh.Length);
                    }
                    return Parse(smdh);
                }
                finally
                {
                    primary?.Dispose();
                }
            }
            catch
            {
                return null;
            }
        }

        // Locates the "icon" entry in the NCCH's plaintext ExeFS and returns its bytes (the SMDH). Assumes the
        // NCCH is no_crypto (the SD-installed boot .app always is); returns null otherwise.
        private static byte[] ReadIconExeFsEntry(FileStream fs)
        {
            if (fs.Length < NcchHeaderSize)
                return null;

            var header = new byte[NcchHeaderSize];
            ReadExactlyAt(fs, 0, header, NcchHeaderSize);
            if (Encoding.ASCII.GetString(header, NcchMagicOffset, 4) != "NCCH")
                return null;
            if ((header[NcchFlagsOffset + 7] & NoCryptoFlag) == 0)
                return null; // still encrypted — caller only reads decrypted SD .apps

            int mediaUnit = 0x200 << header[NcchFlagsOffset + 6];
            long exefsOffset = (long)Le.U32(header, NcchExeFsOffsetOffset) * mediaUnit;
            long exefsSize = (long)Le.U32(header, NcchExeFsOffsetOffset + 4) * mediaUnit;
            if (exefsSize <= 0 || exefsOffset <= 0 || exefsOffset + 0x200 > fs.Length)
                return null;

            var exefsHeader = new byte[0x200];
            ReadExactlyAt(fs, exefsOffset, exefsHeader, 0x200);

            if (!FindIconEntry(exefsHeader, out uint fileOffset, out uint fileSize) || fileSize < Size)
                return null;

            long dataOffset = exefsOffset + 0x200 + fileOffset;
            if (dataOffset + Size > fs.Length)
                return null;

            var smdh = new byte[Size];
            ReadExactlyAt(fs, dataOffset, smdh, Size);
            return smdh;
        }

        // The ExeFS header lists up to 10 files: name[8], offset(u32 LE), size(u32 LE). Offsets are relative to
        // the end of the 0x200 header. Finds the "icon" entry (the SMDH).
        private static bool FindIconEntry(byte[] exefsHeader, out uint iconOffset, out uint iconSize)
        {
            for (int i = 0; i < 10; i++)
            {
                int o = i * 0x10;
                var name = Encoding.ASCII.GetString(exefsHeader, o, 8).TrimEnd('\0');
                if (name != "icon")
                    continue;
                iconOffset = Le.U32(exefsHeader, o + 0x08);
                iconSize = Le.U32(exefsHeader, o + 0x0C);
                return true;
            }
            iconOffset = 0;
            iconSize = 0;
            return false;
        }

        // The short description (the game name) of the first preferred language whose struct is non-empty.
        private static string ReadTitle(byte[] smdh)
        {
            foreach (var lang in TitlePreference)
            {
                int o = TitlesOffset + lang * TitleStructSize;
                if (o + ShortDescSize > smdh.Length)
                    continue;
                var s = DecodeUtf16(smdh, o, ShortDescSize);
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return null;
        }

        // UTF-16LE, NUL-terminated within a fixed field; collapses internal newlines to spaces (some titles wrap
        // across two lines).
        private static string DecodeUtf16(byte[] b, int offset, int byteLen)
        {
            int end = offset;
            int limit = offset + byteLen;
            while (end + 1 < limit && !(b[end] == 0 && b[end + 1] == 0))
                end += 2;
            var s = Encoding.Unicode.GetString(b, offset, end - offset);
            return s.Replace('\n', ' ').Replace("\r", "").Trim();
        }

        // Detiles the 48x48 RGB565 large icon (8x8 Morton-ordered tiles, row-major) into a 24-bit BMP.
        private static byte[] ReadLargeIconBmp(byte[] smdh)
        {
            if (LargeIconOffset + IconDim * IconDim * 2 > smdh.Length)
                return null;

            var rgb = new byte[IconDim * IconDim * 3]; // top-down RGB, converted to BMP order below
            const int tilesPerRow = IconDim / 8;
            for (int y = 0; y < IconDim; y++)
            {
                for (int x = 0; x < IconDim; x++)
                {
                    int tileX = x / 8, tileY = y / 8;
                    int inX = x & 7, inY = y & 7;
                    int tileIndex = tileY * tilesPerRow + tileX;
                    int pixelInTile = Morton(inX, inY);
                    int srcIndex = (tileIndex * 64 + pixelInTile) * 2;
                    ushort px = Le.U16(smdh, LargeIconOffset + srcIndex);

                    int r5 = (px >> 11) & 0x1F, g6 = (px >> 5) & 0x3F, b5 = px & 0x1F;
                    int dst = (y * IconDim + x) * 3;
                    rgb[dst + 0] = (byte)((r5 << 3) | (r5 >> 2));
                    rgb[dst + 1] = (byte)((g6 << 2) | (g6 >> 4));
                    rgb[dst + 2] = (byte)((b5 << 3) | (b5 >> 2));
                }
            }
            return EncodeBmp24(rgb, IconDim, IconDim);
        }

        // Interleaves the low 3 bits of x and y into a tile-local pixel index (3DS texture Morton order).
        private static int Morton(int x, int y) =>
            (x & 1) | ((y & 1) << 1) | ((x & 2) << 1) | ((y & 2) << 2) | ((x & 4) << 2) | ((y & 4) << 3);

        // 24-bit (BGR), bottom-up BMP. Row stride for 48px is 144 bytes (already 4-aligned), so no padding here,
        // but the general 4-byte row alignment is honored for safety.
        private static byte[] EncodeBmp24(byte[] topDownRgb, int width, int height)
        {
            int rowStride = (width * 3 + 3) & ~3;
            int pixelDataSize = rowStride * height;
            int fileSize = 54 + pixelDataSize;

            using (var ms = new MemoryStream(fileSize))
            using (var w = new BinaryWriter(ms))
            {
                // BITMAPFILEHEADER
                w.Write((byte)'B'); w.Write((byte)'M');
                w.Write(fileSize);
                w.Write(0);            // reserved
                w.Write(54);           // pixel data offset
                // BITMAPINFOHEADER
                w.Write(40);           // header size
                w.Write(width);
                w.Write(height);
                w.Write((short)1);     // planes
                w.Write((short)24);    // bpp
                w.Write(0);            // compression (BI_RGB)
                w.Write(pixelDataSize);
                w.Write(2835);         // x pixels/meter (~72 DPI)
                w.Write(2835);         // y pixels/meter
                w.Write(0);            // colors used
                w.Write(0);            // important colors

                var row = new byte[rowStride];
                for (int y = height - 1; y >= 0; y--) // BMP rows are bottom-up
                {
                    for (int x = 0; x < width; x++)
                    {
                        int src = (y * width + x) * 3;
                        int o = x * 3;
                        row[o + 0] = topDownRgb[src + 2]; // B
                        row[o + 1] = topDownRgb[src + 1]; // G
                        row[o + 2] = topDownRgb[src + 0]; // R
                    }
                    w.Write(row, 0, rowStride);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void ReadExactlyAt(FileStream fs, long offset, byte[] buffer, int count) => StreamUtils.ReadExactlyAt(fs, offset, buffer, count, "NCCH stream");
    }
}
