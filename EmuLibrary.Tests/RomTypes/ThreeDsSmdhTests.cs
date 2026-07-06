using EmuLibrary.RomTypes.ThreeDs.Crypto;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.RomTypes
{
    // Pins the SMDH reader: the per-language title pick, the 48x48 RGB565 detile -> 24-bit BMP, and reading the
    // "icon" ExeFS entry out of a no_crypto NCCH (the form an SD-installed boot .app takes).
    public class ThreeDsSmdhTests
    {
        private const int IconDim = 48;
        private const int LargeIconOffset = 0x24C0;

        [Fact]
        public void Parse_RejectsNonSmdh()
        {
            Assert.Null(Smdh.Parse(new byte[Smdh.Size])); // no magic
            Assert.Null(Smdh.Parse(new byte[16]));         // too short
        }

        [Fact]
        public void Parse_ReadsEnglishTitle()
        {
            // English is language index 1; put a different (Japanese, index 0) title there to prove English wins.
            var smdh = BuildSmdh(englishTitle: "Super Mario 3D Land", japaneseTitle: "スーパーマリオ");
            var info = Smdh.Parse(smdh);
            Assert.NotNull(info);
            Assert.Equal("Super Mario 3D Land", info.Title);
        }

        [Fact]
        public void Parse_FallsBackToFirstNonEmptyTitle_WhenEnglishMissing()
        {
            var smdh = BuildSmdh(englishTitle: null, japaneseTitle: "ドンキーコング");
            var info = Smdh.Parse(smdh);
            Assert.Equal("ドンキーコング", info.Title);
        }

        [Fact]
        public void Parse_DetilesIconToBmp_WithCorrectCornerPixels()
        {
            // Fill the whole icon red (RGB565 0xF800), then override the single source element that maps to
            // output pixel (1,0) with blue (0x001F). This both checks RGB565->RGB888 and the Morton detiling.
            var smdh = BuildSmdh("Icon Test", null);
            FillIcon(smdh, 0xF800);
            // output (x=1,y=0): tile (0,0), inX=1,inY=0 -> Morton index 1 -> source element 1.
            WriteLe16(smdh, LargeIconOffset + 1 * 2, 0x001F);

            var bmp = Smdh.Parse(smdh).IconBmp;
            Assert.NotNull(bmp);
            AssertBmpDimensions(bmp, IconDim, IconDim);

            var (r0, g0, b0) = BmpPixel(bmp, 0, 0);
            Assert.Equal((255, 0, 0), (r0, g0, b0)); // red corner
            var (r1, g1, b1) = BmpPixel(bmp, 1, 0);
            Assert.Equal((0, 0, 255), (r1, g1, b1)); // the overridden blue pixel
        }

        [Fact]
        public void TryReadFromNcch_ReadsSmdhFromNoCryptoNcch()
        {
            var smdh = BuildSmdh("Mario Kart 7", null);
            var ncch = BuildNoCryptoNcchWithIcon(smdh);

            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, ncch);
                var info = Smdh.TryReadFromNcch(path);
                Assert.NotNull(info);
                Assert.Equal("Mario Kart 7", info.Title);
                Assert.NotNull(info.IconBmp);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TryReadFromNcch_ReturnsNull_WhenStillEncrypted()
        {
            var smdh = BuildSmdh("Encrypted", null);
            var ncch = BuildNoCryptoNcchWithIcon(smdh);
            ncch[0x188 + 7] &= unchecked((byte)~0x04); // clear the no_crypto flag

            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, ncch);
                Assert.Null(Smdh.TryReadFromNcch(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        #region Builders / helpers

        private static byte[] BuildSmdh(string englishTitle, string japaneseTitle)
        {
            var smdh = new byte[Smdh.Size];
            Encoding.ASCII.GetBytes("SMDH").CopyTo(smdh, 0);
            const int titlesOffset = 0x08, titleStruct = 0x200;
            if (japaneseTitle != null)
                WriteUtf16(smdh, titlesOffset + 0 * titleStruct, japaneseTitle); // language 0
            if (englishTitle != null)
                WriteUtf16(smdh, titlesOffset + 1 * titleStruct, englishTitle);  // language 1
            return smdh;
        }

        private static void FillIcon(byte[] smdh, ushort rgb565)
        {
            for (int i = 0; i < IconDim * IconDim; i++)
                WriteLe16(smdh, LargeIconOffset + i * 2, rgb565);
        }

        // A minimal NCCH: 0x200 plaintext header (no_crypto set), ExeFS at media offset 1 whose first entry is
        // "icon" -> the SMDH.
        private static byte[] BuildNoCryptoNcchWithIcon(byte[] smdh)
        {
            const int mediaUnit = 0x200;
            long exefsOffset = 1 * mediaUnit;          // 0x200
            long iconData = exefsOffset + 0x200;       // 0x400
            int total = (int)(iconData + smdh.Length);
            int exefsBytes = (int)(total - exefsOffset);
            int exefsMu = (exefsBytes + mediaUnit - 1) / mediaUnit;

            var ncch = new byte[total];
            Encoding.ASCII.GetBytes("NCCH").CopyTo(ncch, 0x100);
            ncch[0x188 + 6] = 0;      // media unit shift -> 0x200
            ncch[0x188 + 7] = 0x04;   // no_crypto
            WriteLe32(ncch, 0x1A0, 1);            // ExeFS offset in media units
            WriteLe32(ncch, 0x1A4, (uint)exefsMu); // ExeFS size in media units

            // ExeFS header (at exefsOffset): entry 0 = "icon", offset 0 (after the 0x200 header), size = SMDH.
            int hdr = (int)exefsOffset;
            Encoding.ASCII.GetBytes("icon").CopyTo(ncch, hdr + 0);
            WriteLe32(ncch, hdr + 0x08, 0);
            WriteLe32(ncch, hdr + 0x0C, (uint)smdh.Length);

            Buffer.BlockCopy(smdh, 0, ncch, (int)iconData, smdh.Length);
            return ncch;
        }

        private static void WriteUtf16(byte[] b, int offset, string s)
        {
            var bytes = Encoding.Unicode.GetBytes(s);
            Buffer.BlockCopy(bytes, 0, b, offset, bytes.Length);
        }

        private static void AssertBmpDimensions(byte[] bmp, int w, int h)
        {
            Assert.Equal((byte)'B', bmp[0]);
            Assert.Equal((byte)'M', bmp[1]);
            Assert.Equal(w, BitConverter.ToInt32(bmp, 18));
            Assert.Equal(h, BitConverter.ToInt32(bmp, 22));
            Assert.Equal((short)24, BitConverter.ToInt16(bmp, 28));
        }

        // Reads a pixel from a 24-bit bottom-up BMP at image coordinates (x, y) with (0,0) = top-left.
        private static (int r, int g, int b) BmpPixel(byte[] bmp, int x, int y)
        {
            int width = BitConverter.ToInt32(bmp, 18);
            int height = BitConverter.ToInt32(bmp, 22);
            int dataOffset = BitConverter.ToInt32(bmp, 10);
            int rowStride = (width * 3 + 3) & ~3;
            int bmpRow = height - 1 - y; // bottom-up
            int o = dataOffset + bmpRow * rowStride + x * 3;
            return (bmp[o + 2], bmp[o + 1], bmp[o + 0]); // stored BGR
        }

        private static void WriteLe16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        private static void WriteLe32(byte[] b, int o, uint v)
        {
            for (int i = 0; i < 4; i++)
                b[o + i] = (byte)(v >> (i * 8));
        }

        #endregion
    }
}
