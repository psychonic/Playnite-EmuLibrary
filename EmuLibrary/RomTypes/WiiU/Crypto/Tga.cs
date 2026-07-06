using System;
using System.IO;

namespace EmuLibrary.RomTypes.WiiU.Crypto
{
    // Minimal TGA decoder for the two variants Wii U's meta/iconTex.tga uses: type 2 (uncompressed true-color)
    // and type 10 (RLE true-color), 24- or 32-bpp. WPF can't render TGA, so the icon is transcoded to a 24-bit
    // BMP (same writer shape as Smdh.EncodeBmp24, duplicated here so WiiU doesn't depend on ThreeDs). Honors the
    // image-descriptor origin bits (top-left vs bottom-left, left vs right). Fail-soft: returns null on anything
    // it doesn't understand, like the other icon readers.
    internal static class Tga
    {
        private const int HeaderSize = 18;

        // Decodes a true-color TGA to 24-bit BMP bytes, or null if it isn't a supported TGA.
        public static byte[] TryDecodeToBmp24(byte[] tga)
        {
            try
            {
                if (tga == null || tga.Length < HeaderSize)
                    return null;

                int idLength = tga[0];
                int colorMapType = tga[1];
                int imageType = tga[2];
                if (colorMapType != 0)
                    return null; // true-color only (no palette)
                if (imageType != 2 && imageType != 10)
                    return null;

                int width = tga[12] | (tga[13] << 8);
                int height = tga[14] | (tga[15] << 8);
                int bpp = tga[16];
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                    return null;
                if (bpp != 24 && bpp != 32)
                    return null;

                int bytesPerPixel = bpp / 8;
                bool topToBottom = (tga[17] & 0x20) != 0; // bit 5: 1 = origin at top, 0 = origin at bottom
                bool rightToLeft = (tga[17] & 0x10) != 0; // bit 4: 1 = origin at right

                int dataOffset = HeaderSize + idLength; // color map is empty (colorMapType == 0)
                if (dataOffset > tga.Length)
                    return null;

                int pixelCount = width * height;
                var pixels = new byte[pixelCount * bytesPerPixel]; // raw B,G,R[,A] in storage (origin-relative) order

                if (imageType == 2)
                {
                    if (dataOffset + pixels.Length > tga.Length)
                        return null;
                    Buffer.BlockCopy(tga, dataOffset, pixels, 0, pixels.Length);
                }
                else if (!DecodeRle(tga, dataOffset, pixels, bytesPerPixel))
                {
                    return null;
                }

                // Rearrange into a top-down, left-to-right RGB buffer, applying the origin flags so the output is
                // always top-left origin regardless of how the TGA stored it.
                var rgb = new byte[pixelCount * 3];
                for (int row = 0; row < height; row++)
                {
                    int srcRow = topToBottom ? row : (height - 1 - row);
                    for (int col = 0; col < width; col++)
                    {
                        int srcCol = rightToLeft ? (width - 1 - col) : col;
                        int src = (srcRow * width + srcCol) * bytesPerPixel;
                        int dst = (row * width + col) * 3;
                        rgb[dst + 0] = pixels[src + 2]; // R
                        rgb[dst + 1] = pixels[src + 1]; // G
                        rgb[dst + 2] = pixels[src + 0]; // B
                    }
                }

                return EncodeBmp24(rgb, width, height);
            }
            catch
            {
                return null;
            }
        }

        // Expands RLE-packed true-color pixels (image type 10) into `outPixels`. Packets: a header byte whose top
        // bit selects run (1) vs literal (0) and whose low 7 bits are count-1; a run packet is followed by one
        // pixel repeated, a literal packet by `count` pixels. Runs may cross scanline boundaries.
        private static bool DecodeRle(byte[] tga, int offset, byte[] outPixels, int bytesPerPixel)
        {
            int outPos = 0;
            int pos = offset;
            int total = outPixels.Length;

            while (outPos < total)
            {
                if (pos >= tga.Length)
                    return false;
                int packet = tga[pos++];
                int count = (packet & 0x7F) + 1;

                if ((packet & 0x80) != 0)
                {
                    if (pos + bytesPerPixel > tga.Length)
                        return false;
                    for (int i = 0; i < count && outPos < total; i++)
                    {
                        Buffer.BlockCopy(tga, pos, outPixels, outPos, bytesPerPixel);
                        outPos += bytesPerPixel;
                    }
                    pos += bytesPerPixel;
                }
                else
                {
                    int n = count * bytesPerPixel;
                    if (pos + n > tga.Length)
                        return false;
                    int copy = Math.Min(n, total - outPos);
                    Buffer.BlockCopy(tga, pos, outPixels, outPos, copy);
                    outPos += copy;
                    pos += n;
                }
            }
            return true;
        }

        // 24-bit (BGR), bottom-up BMP from a top-down RGB buffer. Rows are padded to a 4-byte boundary.
        private static byte[] EncodeBmp24(byte[] topDownRgb, int width, int height)
        {
            int rowStride = (width * 3 + 3) & ~3;
            int pixelDataSize = rowStride * height;
            int fileSize = 54 + pixelDataSize;

            using (var ms = new MemoryStream(fileSize))
            using (var w = new BinaryWriter(ms))
            {
                // BITMAPFILEHEADER
                w.Write((byte)'B'); w.Write((byte)'M');
                w.Write(fileSize);
                w.Write(0);            // reserved
                w.Write(54);           // pixel data offset
                // BITMAPINFOHEADER
                w.Write(40);           // header size
                w.Write(width);
                w.Write(height);
                w.Write((short)1);     // planes
                w.Write((short)24);    // bpp
                w.Write(0);            // compression (BI_RGB)
                w.Write(pixelDataSize);
                w.Write(2835);         // x pixels/meter (~72 DPI)
                w.Write(2835);         // y pixels/meter
                w.Write(0);            // colors used
                w.Write(0);            // important colors

                var row = new byte[rowStride];
                for (int y = height - 1; y >= 0; y--) // BMP rows are bottom-up
                {
                    for (int x = 0; x < width; x++)
                    {
                        int src = (y * width + x) * 3;
                        int o = x * 3;
                        row[o + 0] = topDownRgb[src + 2]; // B
                        row[o + 1] = topDownRgb[src + 1]; // G
                        row[o + 2] = topDownRgb[src + 0]; // R
                    }
                    w.Write(row, 0, rowStride);
                }
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
