using System;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // Endian helpers for 3DS formats. The signed blobs (tmd / ticket) are big-endian; the container headers
    // (CIA, NCCH) are little-endian. Both are needed, so each is spelled out explicitly at the call site.
    internal static class Be
    {
        public static ushort U16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        public static uint U32(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        public static ulong U64(byte[] b, int o) =>
            ((ulong)U32(b, o) << 32) | U32(b, o + 4);
    }

    internal static class Le
    {
        public static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        public static uint U32(byte[] b, int o) =>
            b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
        public static ulong U64(byte[] b, int o) =>
            U32(b, o) | ((ulong)U32(b, o + 4) << 32);
    }
}
