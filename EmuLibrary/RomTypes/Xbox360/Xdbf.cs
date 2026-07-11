using EmuLibrary.Util;
using System;

namespace EmuLibrary.RomTypes.Xbox360
{
    // Minimal read-only parser for an XDBF container (the SPA / XEX resource and the GPD save format share the
    // layout). We only need one thing out of it: the embedded title image (a PNG), used as the game's icon.
    //
    // XDBF layout (all big-endian):
    //   0x00  magic "XDBF"
    //   0x04  version (u32)
    //   0x08  entryTableLength (u32)  — number of entry slots (used + free)
    //   0x0C  entryCount (u32)        — number of used entry slots
    //   0x10  freeTableLength (u32)   — number of free-space table slots
    //   0x14  freeCount (u32)
    //   0x18  entry table: { u16 namespace, u64 id, u32 offset, u32 length } * entryTableLength  (18 bytes each)
    //         free table:  { u32 offset, u32 length } * freeTableLength                          (8 bytes each)
    //         data region: entry `offset` values are relative to the start of this region.
    //
    // The title image is namespace 2 (image), id 0x8000 (the title icon shown by the dashboard).
    internal static class Xdbf
    {
        private const int HeaderSize = 0x18;
        private const int EntrySize = 18;
        private const int FreeEntrySize = 8;

        public const ushort ImageNamespace = 2;
        public const ulong TitleImageId = 0x8000;

        private static readonly byte[] Magic = { (byte)'X', (byte)'D', (byte)'B', (byte)'F' };

        // Finds the (namespace 2, id 0x8000) title image and returns its bytes if they are a PNG. Returns false
        // (no throw) on a bad magic, a truncated/oversized table, a missing entry, or non-PNG data.
        public static bool TryReadTitleImage(byte[] data, out byte[] png)
        {
            return TryReadEntry(data, ImageNamespace, TitleImageId, out png)
                && Png.HasPngSignature(png);
        }

        // Locates the (namespace, id) entry and slices its bytes out of the data region. Fail-soft -> false.
        public static bool TryReadEntry(byte[] data, ushort ns, ulong id, out byte[] bytes)
        {
            bytes = null;
            if (data == null || data.Length < HeaderSize)
                return false;
            if (!StartsWith(data, Magic))
                return false;

            uint entryTableLength = ReadU32BE(data, 0x08);
            uint freeTableLength = ReadU32BE(data, 0x10);

            // Guard against corrupt/huge table counts overrunning the buffer.
            long entryTableBytes = (long)entryTableLength * EntrySize;
            long freeTableBytes = (long)freeTableLength * FreeEntrySize;
            long dataRegion = HeaderSize + entryTableBytes + freeTableBytes;
            if (entryTableLength > 0x100000 || freeTableLength > 0x100000 || dataRegion > data.Length)
                return false;

            for (int i = 0; i < entryTableLength; i++)
            {
                int e = HeaderSize + i * EntrySize;
                ushort entryNs = ReadU16BE(data, e + 0x00);
                ulong entryId = ReadU64BE(data, e + 0x02);
                if (entryNs != ns || entryId != id)
                    continue;

                uint offset = ReadU32BE(data, e + 0x0A);
                uint length = ReadU32BE(data, e + 0x0E);
                long start = dataRegion + offset;
                if (length == 0 || start < dataRegion || start + length > data.Length)
                    return false;

                bytes = new byte[length];
                Buffer.BlockCopy(data, (int)start, bytes, 0, (int)length);
                return true;
            }

            return false;
        }

        private static bool StartsWith(byte[] buffer, byte[] prefix)
        {
            if (buffer == null || buffer.Length < prefix.Length)
                return false;
            for (int i = 0; i < prefix.Length; i++)
                if (buffer[i] != prefix[i])
                    return false;
            return true;
        }

        private static ushort ReadU16BE(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

        private static uint ReadU32BE(byte[] b, int o) =>
            (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        private static ulong ReadU64BE(byte[] b, int o) =>
            ((ulong)ReadU32BE(b, o) << 32) | ReadU32BE(b, o + 4);
    }
}
