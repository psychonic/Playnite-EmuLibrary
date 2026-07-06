using System;

namespace EmuLibrary.RomTypes.Xbox360.Disc
{
    // Minimal read-only parser for the XEX2 header's execution id. The execution id lives in the plaintext
    // header region (before any encrypted PE image), so no decryption is needed to read a title's identity.
    //
    // XEX2 layout (all big-endian):
    //   0x00  magic "XEX2"
    //   0x04  module flags
    //   0x08  PE data offset
    //   0x0C  reserved
    //   0x10  security info offset
    //   0x14  optional header (directory) entry count
    //   0x18  directory entries: { uint key, uint value } * count
    //
    // The execution id directory key is 0x00040006; its low byte (0x06) means the value is a file offset to a
    // 6-dword (0x18-byte) struct:
    //   0x00 MediaId  0x04 Version  0x08 BaseVersion  0x0C TitleId
    //   0x10 Platform 0x11 ExecutableType 0x12 DiscNumber 0x13 DiscCount  0x14 SaveGameId
    internal sealed class XexHeader
    {
        public const uint ExecutionIdKey = 0x00040006;

        public uint MediaId { get; private set; }
        public uint Version { get; private set; }
        public uint BaseVersion { get; private set; }
        public uint TitleId { get; private set; }
        public byte Platform { get; private set; }
        public byte ExecutableType { get; private set; }
        public byte DiscNumber { get; private set; }
        public byte DiscCount { get; private set; }

        // Parses the execution id from a buffer holding (at least) the start of a XEX file. Returns false if
        // the magic is wrong, the optional-header table is truncated, or no execution id directory entry exists.
        public static bool TryParse(byte[] xex, out XexHeader header)
        {
            header = null;
            if (xex == null || xex.Length < 0x18)
                return false;
            if (!(xex[0] == (byte)'X' && xex[1] == (byte)'E' && xex[2] == (byte)'X' && xex[3] == (byte)'2'))
                return false;

            uint count = ReadU32BE(xex, 0x14);
            // Guard against a corrupt/huge count overrunning the buffer.
            long tableEnd = 0x18L + (long)count * 8;
            if (count > 0x10000 || tableEnd > xex.Length)
                return false;

            for (int i = 0; i < count; i++)
            {
                int entry = 0x18 + i * 8;
                uint key = ReadU32BE(xex, entry);
                uint value = ReadU32BE(xex, entry + 4);
                if (key != ExecutionIdKey)
                    continue;

                int exec = (int)value;
                if (exec < 0 || exec + 0x14 > xex.Length)
                    return false;

                header = new XexHeader
                {
                    MediaId = ReadU32BE(xex, exec + 0x00),
                    Version = ReadU32BE(xex, exec + 0x04),
                    BaseVersion = ReadU32BE(xex, exec + 0x08),
                    TitleId = ReadU32BE(xex, exec + 0x0C),
                    Platform = xex[exec + 0x10],
                    ExecutableType = xex[exec + 0x11],
                    DiscNumber = xex[exec + 0x12],
                    DiscCount = xex[exec + 0x13],
                };
                return true;
            }

            return false;
        }

        internal static uint ReadU32BE(byte[] b, int offset) =>
            (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
    }
}
