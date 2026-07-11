using EmuLibrary.Util;
using System;
using System.IO;
using System.Text;

namespace EmuLibrary.RomTypes.Xbox360.Stfs
{
    // Read-only STFS (CON/LIVE/PIRS) metadata reader — a flat parse of the header's metadata block, no block
    // table walk and no extraction. STFS metadata is plaintext (keyless), so a title update's / DLC's identity
    // is readable directly. All metadata fields are big-endian.
    //
    //   0x000  magic ("CON ", "LIVE", "PIRS")
    //   0x340  HeaderSize (4)            — selects the hash-table size factor (block addressing)
    //   0x344  ContentType (4)
    //   0x354  MediaId (4)
    //   0x358  Version (4)
    //   0x35C  BaseVersion (4)
    //   0x360  TitleId (4)
    //   0x37C  FileTableBlockCount (2, LITTLE endian) — STFS volume descriptor
    //   0x37E  FileTableBlockNumber (3, LITTLE endian)
    //   0x411  DisplayName (UTF-16BE, NUL-terminated, <= 0x80 bytes)
    //   0x1712 ThumbnailImageSize (4)   — bytes of the package thumbnail PNG
    //   0x171A ThumbnailImage (<= 0x4000) — the package thumbnail PNG (the title's icon)
    internal sealed class StfsPackage
    {
        // Known content types (the high-level ones this RomType cares about).
        public const uint ContentTypeTitleUpdate = 0x000B0000;
        public const uint ContentTypeDlc = 0x00000002;
        public const uint ContentTypeXbla = 0x000D0000;        // digital base (XBLA)
        public const uint ContentTypeGameOnDemand = 0x00007000; // digital base (Games on Demand)

        // Bytes needed to cover the deepest field we read (DisplayName at 0x411 + 0x80).
        public const int HeaderReadSize = 0x500;

        private const int MagicOffset = 0x000;
        private const int HeaderSizeOffset = 0x340;
        private const int ContentTypeOffset = 0x344;
        private const int MediaIdOffset = 0x354;
        private const int VersionOffset = 0x358;
        private const int BaseVersionOffset = 0x35C;
        private const int TitleIdOffset = 0x360;
        private const int FileTableBlockCountOffset = 0x37C; // int16, little-endian
        private const int FileTableBlockNumberOffset = 0x37E; // int24, little-endian
        private const int VolumeDescriptorEnd = 0x381;       // last descriptor byte we read + 1
        private const int DisplayNameOffset = 0x411;
        private const int DisplayNameMaxBytes = 0x80;

        // Package (content) thumbnail: a PNG embedded in the header, used as the title's icon. The size field
        // precedes the image; the image region is capped at 0x4000 bytes.
        private const int ThumbnailSizeOffset = 0x1712;
        private const int ThumbnailOffset = 0x171A;
        private const int ThumbnailMaxBytes = 0x4000;

        // STFS data layout: 0x1000-byte blocks, data starting at 0xC000 (after the header + first hash table).
        // Hash tables are interspersed every 0xAA data blocks (and every 0x70E4 at the next level), doubled when
        // the size factor is 1 — FixBlockNumber accounts for that. For any block < 0xAA the mapping is identity.
        private const int DataStartOffset = 0xC000;
        private const int BlockSize = 0x1000;
        private const int FileEntrySize = 0x40;
        private const int FileEntriesPerBlock = BlockSize / FileEntrySize;

        public uint ContentType { get; private set; }
        public uint MediaId { get; private set; }
        public uint Version { get; private set; }
        public uint BaseVersion { get; private set; }
        public uint TitleId { get; private set; }
        public uint HeaderSize { get; private set; }
        public int FileTableBlockCount { get; private set; }
        public int FileTableBlockNumber { get; private set; }
        public string DisplayName { get; private set; }

        // True if a buffer starts with an STFS magic ("CON ", "LIVE" or "PIRS").
        public static bool IsStfs(byte[] header)
        {
            if (header == null || header.Length < 4)
                return false;
            return Match(header, "CON ") || Match(header, "LIVE") || Match(header, "PIRS");
        }

        // Parses STFS metadata from a header buffer. Returns false if the buffer is too short or lacks a magic.
        public static bool TryParse(byte[] header, out StfsPackage package)
        {
            package = null;
            if (header == null || header.Length < TitleIdOffset + 4)
                return false;
            if (!IsStfs(header))
                return false;

            package = new StfsPackage
            {
                ContentType = ReadU32BE(header, ContentTypeOffset),
                MediaId = ReadU32BE(header, MediaIdOffset),
                Version = ReadU32BE(header, VersionOffset),
                BaseVersion = ReadU32BE(header, BaseVersionOffset),
                TitleId = ReadU32BE(header, TitleIdOffset),
                DisplayName = ReadDisplayName(header),
            };

            // The STFS volume descriptor (needed to locate the file table for ContainsDefaultXex) sits a little
            // past TitleId; only read it when the buffer actually covers it. Left as 0 otherwise (fail-soft).
            if (header.Length >= VolumeDescriptorEnd)
            {
                package.HeaderSize = ReadU32BE(header, HeaderSizeOffset);
                package.FileTableBlockCount = ReadU16LE(header, FileTableBlockCountOffset);
                package.FileTableBlockNumber = ReadU24LE(header, FileTableBlockNumberOffset);
            }
            return true;
        }

        // Reads the leading header bytes of a file and parses its STFS metadata. Returns false (no throw) on
        // any IO error or non-STFS file.
        public static bool TryReadFromFile(string path, out StfsPackage package)
        {
            package = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var header = new byte[HeaderReadSize];
                    int read = 0;
                    while (read < header.Length)
                    {
                        int n = fs.Read(header, read, header.Length - read);
                        if (n <= 0)
                            break;
                        read += n;
                    }
                    if (read < TitleIdOffset + 4)
                        return false;
                    return TryParse(header, out package);
                }
            }
            catch
            {
                return false;
            }
        }

        // True if the package's root directory lists a top-level "default.xex" — the disambiguator between a
        // standalone digital base (XBLIG/App, content type 00000002 + a default.xex) and a plain DLC addon
        // (00000002, no xex). This is the one read that walks into the STFS block area; metadata-only otherwise.
        public bool ContainsDefaultXex(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return ContainsDefaultXex(fs);
            }
            catch
            {
                return false;
            }
        }

        // Inspects only the FIRST file-table block. default.xex is the title's main executable, listed in the
        // root directory's first file-table block for the standalone packages we classify, so the multi-block
        // file-table hash chain isn't walked here. Fail-soft (false) on any anomaly.
        internal bool ContainsDefaultXex(Stream stream)
        {
            if (FileTableBlockNumber < 0 || FileTableBlockCount <= 0)
                return false;

            var block = new byte[BlockSize];
            if (!ReadAt(stream, BlockToOffset(FileTableBlockNumber), block))
                return false;

            for (int i = 0; i < FileEntriesPerBlock; i++)
            {
                int e = i * FileEntrySize;
                int flags = block[e + 0x28];
                int nameLen = flags & 0x3F;
                if (nameLen == 0)
                    continue; // empty entry
                if ((flags & 0x80) != 0)
                    continue; // directory
                int pathIndicator = (block[e + 0x32] << 8) | block[e + 0x33];
                if (pathIndicator != 0xFFFF)
                    continue; // not a root-level file

                var name = Encoding.ASCII.GetString(block, e, Math.Min(nameLen, 0x28));
                if (string.Equals(name, "default.xex", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Real byte offset of an STFS data block, accounting for interspersed hash tables. Identity for the
        // first 0xAA blocks (covers the file table of the standalone packages we probe).
        private long BlockToOffset(int blockNum) =>
            DataStartOffset + (long)FixBlockNumber(blockNum) * BlockSize;

        private int FixBlockNumber(int blockNum)
        {
            // Size factor: 0 when the header rounds to 0xB000, else 1 (hash tables doubled).
            int shift = ((int)(HeaderSize + 0xFFF) & 0xF000) == 0xB000 ? 0 : 1;
            int adjust = 0;
            if (blockNum >= 0xAA)
                adjust += ((blockNum / 0xAA) + 1) << shift;
            if (blockNum >= 0x70E4)
                adjust += ((blockNum / 0x70E4) + 1) << shift;
            return blockNum + adjust;
        }

        // Extracts the embedded package thumbnail PNG (the title's icon) from an STFS file. The size field at
        // 0x1712 (big-endian) gives the PNG length; the image follows at 0x171A. Returns false (no throw) when
        // there's no thumbnail, the size is out of range, or the bytes aren't a PNG. Keyless — the thumbnail
        // sits in the plaintext header region, no block walk needed.
        public static bool TryExtractThumbnail(string path, out byte[] png)
        {
            png = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var sizeBytes = new byte[4];
                    if (!ReadAt(fs, ThumbnailSizeOffset, sizeBytes))
                        return false;
                    int size = (int)ReadU32BE(sizeBytes, 0);
                    if (size <= 0 || size > ThumbnailMaxBytes)
                        return false;

                    var buffer = new byte[size];
                    if (!ReadAt(fs, ThumbnailOffset, buffer))
                        return false;
                    if (!Png.HasPngSignature(buffer))
                        return false;

                    png = buffer;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadAt(Stream s, long offset, byte[] buffer)
        {
            if (offset < 0 || (s.CanSeek && offset + buffer.Length > s.Length))
                return false;
            s.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < buffer.Length)
            {
                int n = s.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return read == buffer.Length;
        }

        private static string ReadDisplayName(byte[] header)
        {
            if (DisplayNameOffset >= header.Length)
                return null;

            int max = Math.Min(DisplayNameMaxBytes, header.Length - DisplayNameOffset);
            // UTF-16BE, NUL-terminated. Stop at the first 0x0000 code unit.
            int len = 0;
            while (len + 1 < max)
            {
                if (header[DisplayNameOffset + len] == 0 && header[DisplayNameOffset + len + 1] == 0)
                    break;
                len += 2;
            }
            if (len == 0)
                return null;

            var name = Encoding.BigEndianUnicode.GetString(header, DisplayNameOffset, len).Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        private static bool Match(byte[] b, string ascii)
        {
            for (int i = 0; i < ascii.Length; i++)
                if (b[i] != (byte)ascii[i])
                    return false;
            return true;
        }

        private static uint ReadU32BE(byte[] b, int o) =>
            (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        private static int ReadU16LE(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8);

        private static int ReadU24LE(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
    }
}
