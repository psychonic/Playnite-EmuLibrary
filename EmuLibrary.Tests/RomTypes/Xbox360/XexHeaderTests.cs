using EmuLibrary.RomTypes.Xbox360.Disc;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Xbox360
{
    public class XexHeaderTests
    {
        private static void WriteU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        // Builds a synthetic XEX2 with a small optional-header directory containing one decoy entry and the
        // execution-id entry pointing at an execution-id struct laid out later in the buffer.
        private static byte[] BuildXex(uint titleId, uint mediaId, uint version)
        {
            var x = new byte[0x100];
            x[0] = (byte)'X'; x[1] = (byte)'E'; x[2] = (byte)'X'; x[3] = (byte)'2';

            WriteU32BE(x, 0x14, 2); // optional header count

            // entry 0 (decoy)
            WriteU32BE(x, 0x18, 0x00010100);
            WriteU32BE(x, 0x1C, 0xDEADBEEF);
            // entry 1: execution id -> offset 0x80
            WriteU32BE(x, 0x20, XexHeader.ExecutionIdKey);
            WriteU32BE(x, 0x24, 0x80);

            // execution id struct @ 0x80
            WriteU32BE(x, 0x80 + 0x00, mediaId);
            WriteU32BE(x, 0x80 + 0x04, version);
            WriteU32BE(x, 0x80 + 0x08, version); // base version
            WriteU32BE(x, 0x80 + 0x0C, titleId);
            x[0x80 + 0x12] = 1; // disc number
            x[0x80 + 0x13] = 1; // disc count
            return x;
        }

        [Fact]
        public void ParsesExecutionId()
        {
            var x = BuildXex(0x5841085E, 0x0039F097, 0x00000104);

            Assert.True(XexHeader.TryParse(x, out var h));
            Assert.Equal(0x5841085EU, h.TitleId);
            Assert.Equal(0x0039F097U, h.MediaId);
            Assert.Equal(0x00000104U, h.Version);
            Assert.Equal(1, h.DiscNumber);
            Assert.Equal(1, h.DiscCount);
        }

        [Fact]
        public void RejectsBadMagic()
        {
            var x = BuildXex(1, 2, 3);
            x[0] = (byte)'N';
            Assert.False(XexHeader.TryParse(x, out _));
        }

        [Fact]
        public void RejectsMissingExecutionId()
        {
            var x = new byte[0x40];
            x[0] = (byte)'X'; x[1] = (byte)'E'; x[2] = (byte)'X'; x[3] = (byte)'2';
            WriteU32BE(x, 0x14, 1);
            WriteU32BE(x, 0x18, 0x00010100); // a non-execution-id entry
            WriteU32BE(x, 0x1C, 0);
            Assert.False(XexHeader.TryParse(x, out _));
        }
    }
}
