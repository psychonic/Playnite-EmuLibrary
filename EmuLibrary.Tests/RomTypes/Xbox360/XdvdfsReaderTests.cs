using EmuLibrary.RomTypes.Xbox360.Disc;
using System.IO;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Xbox360
{
    public class XdvdfsReaderTests
    {
        private const int SectorSize = 0x800;

        private static void WriteU32LE(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static void WriteU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        // Builds a minimal raw XISO (partition base offset 0): a volume descriptor at sector 32, a one-entry
        // root directory at sector 33, and a default.xex (a XEX2 with an execution id) at sector 34.
        private static byte[] BuildMiniIso(uint titleId, uint mediaId, uint version)
        {
            var iso = new byte[0x12000];

            // Volume descriptor @ sector 32 (0x10000).
            int vd = 32 * SectorSize;
            var sig = System.Text.Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");
            System.Array.Copy(sig, 0, iso, vd, sig.Length);
            WriteU32LE(iso, vd + 0x14, 33);     // root dir sector
            WriteU32LE(iso, vd + 0x18, SectorSize); // root dir size

            // Root directory @ sector 33 (0x10800): single entry "default.xex".
            int rd = 33 * SectorSize;
            WriteU16LE(iso, rd + 0x00, 0); // left
            WriteU16LE(iso, rd + 0x02, 0); // right
            WriteU32LE(iso, rd + 0x04, 34); // start sector
            WriteU32LE(iso, rd + 0x08, 0x100); // file size
            iso[rd + 0x0C] = 0x20; // attributes (archive)
            var name = "default.xex";
            iso[rd + 0x0D] = (byte)name.Length;
            for (int i = 0; i < name.Length; i++)
                iso[rd + 0x0E + i] = (byte)name[i];

            // default.xex @ sector 34 (0x11000).
            int xe = 34 * SectorSize;
            iso[xe + 0] = (byte)'X'; iso[xe + 1] = (byte)'E'; iso[xe + 2] = (byte)'X'; iso[xe + 3] = (byte)'2';
            WriteU32BE(iso, xe + 0x14, 1); // optional header count
            WriteU32BE(iso, xe + 0x18, XexHeader.ExecutionIdKey);
            WriteU32BE(iso, xe + 0x1C, 0x80); // execution id offset within the xex
            WriteU32BE(iso, xe + 0x80 + 0x00, mediaId);
            WriteU32BE(iso, xe + 0x80 + 0x04, version);
            WriteU32BE(iso, xe + 0x80 + 0x08, version);
            WriteU32BE(iso, xe + 0x80 + 0x0C, titleId);

            return iso;
        }

        private static void WriteU16LE(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        [Fact]
        public void LocatesVolumeAndReadsDefaultXexExecutionId()
        {
            var iso = BuildMiniIso(0x5841085E, 0x0039F097, 0x00000104);

            using (var ms = new MemoryStream(iso))
            using (var reader = XdvdfsReader.TryOpen(ms, leaveOpen: true))
            {
                Assert.NotNull(reader);
                var xexBytes = reader.TryReadDefaultXex(0x1000);
                Assert.NotNull(xexBytes);
                Assert.True(XexHeader.TryParse(xexBytes, out var xex));
                Assert.Equal(0x5841085EU, xex.TitleId);
                Assert.Equal(0x0039F097U, xex.MediaId);
                Assert.Equal(0x00000104U, xex.Version);
            }
        }

        [Fact]
        public void ReturnsNullForNonVolumeStream()
        {
            using (var ms = new MemoryStream(new byte[0x12000]))
            {
                Assert.Null(XdvdfsReader.TryOpen(ms, leaveOpen: true));
            }
        }
    }
}
