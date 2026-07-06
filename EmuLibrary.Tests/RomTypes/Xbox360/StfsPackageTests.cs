using EmuLibrary.RomTypes.Xbox360.Stfs;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Xbox360
{
    public class StfsPackageTests
    {
        // Builds a synthetic STFS header with the metadata fields written (big-endian) at the pinned offsets.
        private static byte[] BuildHeader(string magic, uint contentType, uint mediaId, uint version, uint baseVersion, uint titleId, string displayName)
        {
            var h = new byte[StfsPackage.HeaderReadSize];
            for (int i = 0; i < magic.Length; i++)
                h[i] = (byte)magic[i];

            WriteU32BE(h, 0x344, contentType);
            WriteU32BE(h, 0x354, mediaId);
            WriteU32BE(h, 0x358, version);
            WriteU32BE(h, 0x35C, baseVersion);
            WriteU32BE(h, 0x360, titleId);

            if (displayName != null)
            {
                var bytes = Encoding.BigEndianUnicode.GetBytes(displayName);
                System.Array.Copy(bytes, 0, h, 0x411, bytes.Length);
            }
            return h;
        }

        private static void WriteU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        [Fact]
        public void ParsesTitleUpdateMetadata()
        {
            var h = BuildHeader("LIVE", StfsPackage.ContentTypeTitleUpdate, 0x0039F097, 0x00000104, 0x00000101, 0x5841085E, "Marathon: Durandal Title Update #1");

            Assert.True(StfsPackage.TryParse(h, out var pkg));
            Assert.Equal(StfsPackage.ContentTypeTitleUpdate, pkg.ContentType);
            Assert.Equal(0x0039F097U, pkg.MediaId);
            Assert.Equal(0x00000104U, pkg.Version);
            Assert.Equal(0x00000101U, pkg.BaseVersion);
            Assert.Equal(0x5841085EU, pkg.TitleId);
            Assert.Equal("Marathon: Durandal Title Update #1", pkg.DisplayName);
        }

        [Theory]
        [InlineData("CON ")]
        [InlineData("LIVE")]
        [InlineData("PIRS")]
        public void AcceptsAllStfsMagics(string magic)
        {
            var h = BuildHeader(magic, StfsPackage.ContentTypeDlc, 0, 0, 0, 0x415608D8, null);
            Assert.True(StfsPackage.IsStfs(h));
            Assert.True(StfsPackage.TryParse(h, out var pkg));
            Assert.Equal(StfsPackage.ContentTypeDlc, pkg.ContentType);
            Assert.Equal(0x415608D8U, pkg.TitleId);
        }

        [Fact]
        public void RejectsNonStfsBuffer()
        {
            var h = new byte[StfsPackage.HeaderReadSize];
            h[0] = (byte)'X'; h[1] = (byte)'E'; h[2] = (byte)'X'; h[3] = (byte)'2';
            Assert.False(StfsPackage.IsStfs(h));
            Assert.False(StfsPackage.TryParse(h, out _));
        }

        [Fact]
        public void RejectsTruncatedBuffer()
        {
            Assert.False(StfsPackage.TryParse(new byte[0x10], out _));
        }

        // Builds a full STFS image: a header with a one-block file table at block 0 (offset 0xC000), and that
        // block populated with root-level file entries for the given names. Header size 0xB000 => size factor 0,
        // and block 0 maps identically, so the file table lands at exactly 0xC000.
        private static byte[] BuildImageWithRootFiles(uint contentType, params string[] rootFileNames)
        {
            const int fileTableOffset = 0xC000;
            var buf = new byte[fileTableOffset + 0x1000];

            buf[0] = (byte)'L'; buf[1] = (byte)'I'; buf[2] = (byte)'V'; buf[3] = (byte)'E';
            WriteU32BE(buf, 0x340, 0xB000);        // HeaderSize
            WriteU32BE(buf, 0x344, contentType);
            WriteU32BE(buf, 0x360, 0x5841098F);    // TitleId

            // Volume descriptor: file table block count = 1 (LE16), block number = 0 (LE24).
            buf[0x37C] = 1; buf[0x37D] = 0;
            buf[0x37E] = 0; buf[0x37F] = 0; buf[0x380] = 0;

            for (int i = 0; i < rootFileNames.Length; i++)
            {
                int e = fileTableOffset + i * 0x40;
                var nm = Encoding.ASCII.GetBytes(rootFileNames[i]);
                Array.Copy(nm, 0, buf, e, nm.Length);
                buf[e + 0x28] = (byte)(nm.Length & 0x3F); // name length, no directory bit
                buf[e + 0x32] = 0xFF; buf[e + 0x33] = 0xFF; // path indicator 0xFFFF = root
            }
            return buf;
        }

        [Fact]
        public void ContainsDefaultXex_TrueWhenRootHasDefaultXex()
        {
            var img = BuildImageWithRootFiles(StfsPackage.ContentTypeDlc, "default.xex", "game.bin");
            Assert.True(StfsPackage.TryParse(img, out var pkg));
            Assert.True(pkg.ContainsDefaultXex(new MemoryStream(img)));
        }

        [Fact]
        public void ContainsDefaultXex_FalseWhenNoDefaultXex()
        {
            var img = BuildImageWithRootFiles(StfsPackage.ContentTypeDlc, "readme.txt", "addon.bin");
            Assert.True(StfsPackage.TryParse(img, out var pkg));
            Assert.False(pkg.ContainsDefaultXex(new MemoryStream(img)));
        }

        [Fact]
        public void ContainsDefaultXex_IgnoresNonRootEntries()
        {
            var img = BuildImageWithRootFiles(StfsPackage.ContentTypeDlc, "default.xex");
            // Demote the single entry to a subfolder (path indicator 0 = child of dir 0, not root).
            int e = 0xC000;
            img[e + 0x32] = 0x00; img[e + 0x33] = 0x00;
            Assert.True(StfsPackage.TryParse(img, out var pkg));
            Assert.False(pkg.ContainsDefaultXex(new MemoryStream(img)));
        }

        private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // Builds an STFS image carrying a package thumbnail: size (BE) at 0x1712, bytes at 0x171A.
        private static byte[] BuildImageWithThumbnail(byte[] thumbnail)
        {
            var buf = new byte[0x171A + 0x4000];
            buf[0] = (byte)'L'; buf[1] = (byte)'I'; buf[2] = (byte)'V'; buf[3] = (byte)'E';
            WriteU32BE(buf, 0x1712, (uint)thumbnail.Length);
            Array.Copy(thumbnail, 0, buf, 0x171A, thumbnail.Length);
            return buf;
        }

        private static byte[] WithTempFile(byte[] content, Func<string, byte[]> act)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".stfs");
            try
            {
                File.WriteAllBytes(path, content);
                return act(path);
            }
            finally
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void TryExtractThumbnail_ReturnsPng_WhenPresent()
        {
            var png = new byte[64];
            Array.Copy(PngMagic, png, PngMagic.Length);
            var img = BuildImageWithThumbnail(png);

            var extracted = WithTempFile(img, path =>
            {
                Assert.True(StfsPackage.TryExtractThumbnail(path, out var bytes));
                return bytes;
            });

            Assert.Equal(png, extracted);
        }

        [Fact]
        public void TryExtractThumbnail_FalseWhenSizeZero()
        {
            var img = BuildImageWithThumbnail(new byte[0]); // size field stays 0
            WithTempFile(img, path =>
            {
                Assert.False(StfsPackage.TryExtractThumbnail(path, out var bytes));
                Assert.Null(bytes);
                return bytes;
            });
        }

        [Fact]
        public void TryExtractThumbnail_FalseWhenNotPng()
        {
            var notPng = new byte[64]; // no PNG signature
            notPng[0] = 0xFF;
            var img = BuildImageWithThumbnail(notPng);
            WithTempFile(img, path =>
            {
                Assert.False(StfsPackage.TryExtractThumbnail(path, out var bytes));
                Assert.Null(bytes);
                return bytes;
            });
        }
    }
}
