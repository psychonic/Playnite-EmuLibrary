using System;
using System.IO;
using System.Security.Cryptography;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // Random-access reader for a titlekey-AES-128-CBC-encrypted content (the outer layer a CIA/CDN content
    // carries; IV of the first block = the content index as a 16-byte big-endian value). CBC decryption only
    // needs the immediately-preceding ciphertext block as the IV, so any block range can be decrypted on its own
    // without walking the content from the start — a small read deep into a multi-GB content stays cheap. This is
    // what lets the scanner read just a title's SMDH icon without materializing the whole decrypted content.
    internal static class ContentCbc
    {
        // Reads [offsetInContent, offsetInContent+length) of the content, titlekey-CBC-decrypted. The content
        // begins at contentBaseOffset within filePath and is contentSize bytes. Bytes past the content end are
        // not returned (the result may be shorter than length); an out-of-range start yields an empty array.
        public static byte[] ReadRange(string filePath, long contentBaseOffset, ushort contentIndex,
            ulong contentSize, byte[] titleKey, long offsetInContent, int length)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ReadRange(fs, contentBaseOffset, contentIndex, contentSize, titleKey, offsetInContent, length);
        }

        // As above, but over an already-open seekable stream (not disposed here). Lets a caller read from a
        // decompressed .zcia prefix buffer without materializing the whole file — the icon region sits near the
        // front of content, so only a small prefix need be present.
        public static byte[] ReadRange(Stream stream, long contentBaseOffset, ushort contentIndex,
            ulong contentSize, byte[] titleKey, long offsetInContent, int length)
        {
            if (length <= 0 || offsetInContent < 0 || (ulong)offsetInContent >= contentSize)
                return Array.Empty<byte>();

            long end = offsetInContent + length;
            if ((ulong)end > contentSize)
                end = (long)contentSize;

            long blockStart = offsetInContent & ~15L;          // round down to a 16-byte block
            long blockEnd = (end + 15) & ~15L;                  // round up to a 16-byte block
            int dataLen = (int)(blockEnd - blockStart);

            var iv = new byte[16];
            if (blockStart == 0)
            {
                // First block of the content uses IV = content index, 16-byte big-endian.
                iv[0] = (byte)(contentIndex >> 8);
                iv[1] = (byte)contentIndex;
            }
            else
            {
                // Otherwise IV = the ciphertext block immediately before the range.
                stream.Seek(contentBaseOffset + blockStart - 16, SeekOrigin.Begin);
                ReadExactlyInto(stream, iv, 16);
            }

            var enc = new byte[dataLen];
            stream.Seek(contentBaseOffset + blockStart, SeekOrigin.Begin);
            ReadExactlyInto(stream, enc, dataLen);

            using (var aes = Aes.Create())
            {
                aes.Key = titleKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var dec = aes.CreateDecryptor())
                {
                    var plain = new byte[dataLen];
                    dec.TransformBlock(enc, 0, dataLen, plain, 0);

                    int sliceStart = (int)(offsetInContent - blockStart);
                    int sliceLen = (int)(end - offsetInContent);
                    var result = new byte[sliceLen];
                    Buffer.BlockCopy(plain, sliceStart, result, 0, sliceLen);
                    return result;
                }
            }
        }

        private static void ReadExactlyInto(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException("Unexpected end of content stream.");
                read += n;
            }
        }
    }
}
