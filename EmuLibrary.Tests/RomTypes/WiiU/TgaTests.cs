using EmuLibrary.RomTypes.WiiU.Crypto;
using System;
using System.Collections.Generic;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.WiiU
{
    // Round-trips synthetic true-color TGAs (uncompressed type 2 and RLE type 10, both origin orientations)
    // through Tga.TryDecodeToBmp24 and checks the decoded pixels land where they should.
    public class TgaTests
    {
        // Distinct RGB per pixel so any row/column mix-up or origin flip is caught.
        private static byte[] Rgb(int x, int y) => new byte[] { (byte)(10 + x * 30), (byte)(20 + y * 30), (byte)(40 + x + y) };

        [Fact]
        public void Type2_24bpp_BottomLeftOrigin_DecodesTopLeft()
        {
            var tga = BuildType2(3, 2, bpp: 24, topOrigin: false);
            AssertDecodesToExpectedPixels(tga, 3, 2);
        }

        [Fact]
        public void Type2_24bpp_TopLeftOrigin_DecodesTopLeft()
        {
            var tga = BuildType2(3, 2, bpp: 24, topOrigin: true);
            AssertDecodesToExpectedPixels(tga, 3, 2);
        }

        [Fact]
        public void Type2_32bpp_DropsAlpha()
        {
            var tga = BuildType2(2, 2, bpp: 32, topOrigin: false);
            AssertDecodesToExpectedPixels(tga, 2, 2);
        }

        [Fact]
        public void Type10_Rle_24bpp_DecodesTopLeft()
        {
            var tga = BuildType10(4, 3, bpp: 24, topOrigin: false);
            AssertDecodesToExpectedPixels(tga, 4, 3);
        }

        [Fact]
        public void Type10_Rle_TopOrigin_DecodesTopLeft()
        {
            var tga = BuildType10(4, 3, bpp: 24, topOrigin: true);
            AssertDecodesToExpectedPixels(tga, 4, 3);
        }

        [Fact]
        public void ProducesValidBmp24Header()
        {
            var bmp = Tga.TryDecodeToBmp24(BuildType2(4, 4, 24, topOrigin: false));
            Assert.NotNull(bmp);
            Assert.Equal((byte)'B', bmp[0]);
            Assert.Equal((byte)'M', bmp[1]);
            Assert.Equal(40, BitConverter.ToInt32(bmp, 14));   // BITMAPINFOHEADER size
            Assert.Equal(4, BitConverter.ToInt32(bmp, 18));    // width
            Assert.Equal(4, BitConverter.ToInt32(bmp, 22));    // height
            Assert.Equal((short)24, BitConverter.ToInt16(bmp, 28));
            Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2)); // file size field matches
        }

        [Theory]
        [InlineData(null)]
        [InlineData(new byte[0])]
        public void Rejects_TooShort(byte[] input) => Assert.Null(Tga.TryDecodeToBmp24(input));

        [Fact]
        public void Rejects_PalettedAndUnknownTypes()
        {
            var paletted = BuildType2(2, 2, 24, false);
            paletted[1] = 1; // color-map type != 0
            Assert.Null(Tga.TryDecodeToBmp24(paletted));

            var grayscale = BuildType2(2, 2, 24, false);
            grayscale[2] = 3; // image type 3 (grayscale) unsupported
            Assert.Null(Tga.TryDecodeToBmp24(grayscale));
        }

        // Reads a pixel out of a 24-bit bottom-up BMP at top-left-origin coordinates.
        private static byte[] BmpPixel(byte[] bmp, int width, int height, int x, int y)
        {
            int rowStride = (width * 3 + 3) & ~3;
            int bmpRow = height - 1 - y; // BMP rows are bottom-up
            int o = 54 + bmpRow * rowStride + x * 3;
            return new byte[] { bmp[o + 2], bmp[o + 1], bmp[o + 0] }; // stored B,G,R -> return R,G,B
        }

        private static void AssertDecodesToExpectedPixels(byte[] tga, int width, int height)
        {
            var bmp = Tga.TryDecodeToBmp24(tga);
            Assert.NotNull(bmp);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    Assert.Equal(Rgb(x, y), BmpPixel(bmp, width, height, x, y));
        }

        // Uncompressed true-color TGA. Pixels are written in storage order dictated by the origin bit.
        private static byte[] BuildType2(int width, int height, int bpp, bool topOrigin)
        {
            var buf = new List<byte>();
            AddHeader(buf, imageType: 2, width, height, bpp, topOrigin);
            int bytesPerPixel = bpp / 8;
            for (int srcRow = 0; srcRow < height; srcRow++)
            {
                int y = topOrigin ? srcRow : (height - 1 - srcRow);
                for (int x = 0; x < width; x++)
                    buf.AddRange(PixelBgr(x, y, bytesPerPixel));
            }
            return buf.ToArray();
        }

        // RLE true-color TGA. Each pixel is emitted as its own 1-pixel raw packet (exercises the literal path)
        // plus, on the first pixel of each row, a 1-count run packet of the same pixel (exercises the run path).
        private static byte[] BuildType10(int width, int height, int bpp, bool topOrigin)
        {
            var buf = new List<byte>();
            AddHeader(buf, imageType: 10, width, height, bpp, topOrigin);
            int bytesPerPixel = bpp / 8;
            for (int srcRow = 0; srcRow < height; srcRow++)
            {
                int y = topOrigin ? srcRow : (height - 1 - srcRow);
                for (int x = 0; x < width; x++)
                {
                    var px = PixelBgr(x, y, bytesPerPixel);
                    if (x == 0)
                    {
                        buf.Add(0x80); // run packet, count-1 = 0 -> one pixel
                        buf.AddRange(px);
                    }
                    else
                    {
                        buf.Add(0x00); // raw packet, count-1 = 0 -> one literal pixel
                        buf.AddRange(px);
                    }
                }
            }
            return buf.ToArray();
        }

        private static byte[] PixelBgr(int x, int y, int bytesPerPixel)
        {
            var rgb = Rgb(x, y);
            var px = new byte[bytesPerPixel];
            px[0] = rgb[2]; // B
            px[1] = rgb[1]; // G
            px[2] = rgb[0]; // R
            if (bytesPerPixel == 4)
                px[3] = 0xFF; // alpha, ignored by the decoder
            return px;
        }

        private static void AddHeader(List<byte> buf, int imageType, int width, int height, int bpp, bool topOrigin)
        {
            buf.Add(0);                      // id length
            buf.Add(0);                      // color map type
            buf.Add((byte)imageType);
            buf.AddRange(new byte[5]);       // color map spec
            buf.AddRange(new byte[4]);       // x/y origin
            buf.Add((byte)(width & 0xFF)); buf.Add((byte)(width >> 8));
            buf.Add((byte)(height & 0xFF)); buf.Add((byte)(height >> 8));
            buf.Add((byte)bpp);
            buf.Add((byte)(topOrigin ? 0x20 : 0x00)); // image descriptor: bit 5 = top origin
        }
    }
}
