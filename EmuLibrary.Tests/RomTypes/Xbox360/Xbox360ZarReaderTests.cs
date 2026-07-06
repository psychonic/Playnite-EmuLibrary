using EmuLibrary.RomTypes.Xbox360;
using EmuLibrary.RomTypes.Xbox360.Disc;
using EmuLibrary.Util.ZArchive;
using System.IO;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Xbox360
{
    // A Xenia .zar is a ZArchive of an extracted, decrypted dump; Xenia boots \default.xex, so the plugin reads
    // the title's identity from the archive's top-level default.xex — the same plaintext XEX2 execution id the
    // disc path parses. These tests build a .zar in memory (via ZArchiveWriter) and verify the reader recovers
    // the header for XexHeader to parse, and that a .zar without a root default.xex is rejected.
    public class Xbox360ZarReaderTests
    {
        private static void WriteU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        private static byte[] BuildXex(uint titleId, uint mediaId, uint version)
        {
            var x = new byte[0x100];
            x[0] = (byte)'X'; x[1] = (byte)'E'; x[2] = (byte)'X'; x[3] = (byte)'2';
            WriteU32BE(x, 0x14, 1); // one optional-header entry
            WriteU32BE(x, 0x18, XexHeader.ExecutionIdKey);
            WriteU32BE(x, 0x1C, 0x80); // execution-id struct offset
            WriteU32BE(x, 0x80 + 0x00, mediaId);
            WriteU32BE(x, 0x80 + 0x04, version);
            WriteU32BE(x, 0x80 + 0x08, version);
            WriteU32BE(x, 0x80 + 0x0C, titleId);
            return x;
        }

        // Builds a .zar in memory holding the given root files.
        private static MemoryStream BuildZar(params (string path, byte[] data)[] files)
        {
            var ms = new MemoryStream();
            using (var w = new ZArchiveWriter(ms))
            {
                foreach (var f in files)
                {
                    int slash = f.path.LastIndexOf('/');
                    if (slash > 0)
                        w.MakeDir(f.path.Substring(0, slash), true);
                    w.StartNewFile(f.path);
                    w.AppendData(f.data, 0, f.data.Length);
                }
                w.Complete();
            }
            ms.Position = 0;
            return ms;
        }

        [Fact]
        public void ReadsExecutionId_FromRootDefaultXex()
        {
            var xex = BuildXex(0x4D5307D5, 0x76E9DF5B, 0x00000104);
            using (var zar = BuildZar(("default.xex", xex), ("readme.txt", new byte[] { 1, 2, 3 })))
            {
                Assert.True(Xbox360ZarReader.TryReadDefaultXex(zar, 0x40000, out var bytes));
                Assert.True(XexHeader.TryParse(bytes, out var h));
                Assert.Equal(0x4D5307D5U, h.TitleId);
                Assert.Equal(0x76E9DF5BU, h.MediaId);
                Assert.Equal(0x00000104U, h.Version);
            }
        }

        [Fact]
        public void ReadsPrefixOnly_WhenXexLargerThanCap()
        {
            // default.xex padded well past the cap; the execution id still sits within the requested prefix.
            var xex = new byte[512 * 1024];
            var header = BuildXex(0x584108F1, 0x00112233, 0x00000200);
            System.Buffer.BlockCopy(header, 0, xex, 0, header.Length);

            using (var zar = BuildZar(("default.xex", xex)))
            {
                Assert.True(Xbox360ZarReader.TryReadDefaultXex(zar, 0x40000, out var bytes));
                Assert.Equal(0x40000, bytes.Length); // only the prefix was inflated
                Assert.True(XexHeader.TryParse(bytes, out var h));
                Assert.Equal(0x584108F1U, h.TitleId);
            }
        }

        [Fact]
        public void ReturnsFalse_WhenNoRootDefaultXex()
        {
            // A nested default.xex doesn't count — Xenia only mounts the root, so neither do we.
            var xex = BuildXex(1, 2, 3);
            using (var zar = BuildZar(("game/default.xex", xex), ("readme.txt", new byte[] { 9 })))
            {
                Assert.False(Xbox360ZarReader.TryReadDefaultXex(zar, 0x40000, out var bytes));
                Assert.Null(bytes);
            }
        }

        [Fact]
        public void ReturnsFalse_WhenNotAZArchive()
        {
            using (var junk = new MemoryStream(new byte[200]))
                Assert.False(Xbox360ZarReader.TryReadDefaultXex(junk, 0x40000, out _));
        }
    }
}
