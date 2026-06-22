using EmuLibrary.RomTypes.WiiU.Crypto;
using System;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.WiiU
{
    // Validates the disc container layer: the WUX sector-dedup mapping (logical -> physical, cross-sector
    // reads, zero-fill past end) and the disc-key AES-CBC decryption layer (fixed and per-block IVs).
    public class WiiUDiscTests
    {
        // In-memory DiscReader over a raw byte blob, for exercising ReadDecrypted without a real disc.
        private sealed class MemDisc : DiscReader
        {
            private readonly byte[] _data;
            public MemDisc(byte[] data) { _data = data; }
            public override long Length => _data.Length;
            public override void ReadRaw(long offset, byte[] buffer, int bufferOffset, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    long p = offset + i;
                    buffer[bufferOffset + i] = (p >= 0 && p < _data.Length) ? _data[p] : (byte)0;
                }
            }
            public override void Dispose() { }
        }

        [Fact]
        public void Wux_MapsDeduplicatedSectors_AndZeroFillsPastEnd()
        {
            const int sectorSize = 16;
            var a = new byte[sectorSize];
            var b = new byte[sectorSize];
            for (int i = 0; i < sectorSize; i++) { a[i] = (byte)(0xA0 + i); b[i] = (byte)(0xB0 + i); }

            // Logical layout: sector0=A, sector1=B, sector2=A (sector 0 and 2 dedup to one physical sector).
            long uncompressedSize = 3 * sectorSize;
            var indexTable = new uint[] { 0, 1, 0 };
            long offsetSectorArray = 0x30; // align(0x20 + 3*4, 16)

            var file = new byte[offsetSectorArray + 2 * sectorSize];
            PutLeU32(file, 0x00, 0x30585557); // 'WUX0'
            PutLeU32(file, 0x04, 0x1099d02e);
            PutLeU32(file, 0x08, sectorSize);
            PutLeU32(file, 0x0C, 0);
            PutLeU64(file, 0x10, (ulong)uncompressedSize);
            for (int i = 0; i < indexTable.Length; i++)
                PutLeU32(file, 0x20 + i * 4, indexTable[i]);
            Buffer.BlockCopy(a, 0, file, (int)offsetSectorArray, sectorSize);
            Buffer.BlockCopy(b, 0, file, (int)offsetSectorArray + sectorSize, sectorSize);

            var tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmp, file);
                using (var reader = DiscReader.Open(tmp))
                {
                    Assert.IsType<WuxReader>(reader);
                    Assert.Equal(uncompressedSize, reader.Length);

                    // Full logical image == A ++ B ++ A.
                    var all = reader.ReadRaw(0, (int)uncompressedSize);
                    AssertRange(a, all, 0);
                    AssertRange(b, all, sectorSize);
                    AssertRange(a, all, 2 * sectorSize);

                    // Cross-sector read spanning the sector0/sector1 boundary.
                    var cross = reader.ReadRaw(8, 16);
                    for (int i = 0; i < 8; i++) Assert.Equal(a[8 + i], cross[i]);
                    for (int i = 0; i < 8; i++) Assert.Equal(b[i], cross[8 + i]);

                    // Read past the end zero-fills.
                    var tail = reader.ReadRaw(uncompressedSize - 8, 16);
                    for (int i = 0; i < 8; i++) Assert.Equal(a[8 + i], tail[i]);
                    for (int i = 8; i < 16; i++) Assert.Equal(0, tail[i]);
                }
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void ReadDecrypted_FixedIv_RoundTrips()
        {
            var key = RandomBytes(16, 1);
            var plaintext = RandomBytes(0x100, 2);

            // One 0x10000 chunk, CBC-encrypted with a zero IV; ReadDecrypted should recover the prefix.
            var chunk = new byte[0x10000];
            Buffer.BlockCopy(plaintext, 0, chunk, 0, plaintext.Length);
            var cipher = AesCbcEncrypt(key, new byte[16], chunk);

            using (var disc = new MemDisc(cipher))
            {
                var dec = disc.ReadDecrypted(0, 0, plaintext.Length, key, null, true);
                Assert.Equal(plaintext, dec);
            }
        }

        [Fact]
        public void ReadDecrypted_PerBlockIv_RoundTrips()
        {
            var key = RandomBytes(16, 3);
            var plaintext = RandomBytes(0x20000, 4); // two blocks

            // Each 0x10000 block is encrypted with its own IV = { 0*8, BE(fileOffset >> 16) }.
            var iv0 = new byte[16];                 // fileOffset 0
            var iv1 = new byte[16]; iv1[15] = 1;    // fileOffset 0x10000 -> >>16 == 1

            var block0 = new byte[0x10000];
            var block1 = new byte[0x10000];
            Buffer.BlockCopy(plaintext, 0, block0, 0, 0x10000);
            Buffer.BlockCopy(plaintext, 0x10000, block1, 0, 0x10000);

            var cipher = new byte[0x20000];
            Buffer.BlockCopy(AesCbcEncrypt(key, iv0, block0), 0, cipher, 0, 0x10000);
            Buffer.BlockCopy(AesCbcEncrypt(key, iv1, block1), 0, cipher, 0x10000, 0x10000);

            using (var disc = new MemDisc(cipher))
            {
                var dec = disc.ReadDecrypted(0, 0, plaintext.Length, key, null, false);
                Assert.Equal(plaintext, dec);
            }
        }

        private static void AssertRange(byte[] expected, byte[] actual, int actualOffset)
        {
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], actual[actualOffset + i]);
        }

        private static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] data)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var e = aes.CreateEncryptor(key, iv))
                {
                    var o = new byte[data.Length];
                    e.TransformBlock(data, 0, data.Length, o, 0);
                    return o;
                }
            }
        }

        private static byte[] RandomBytes(int n, ulong seed)
        {
            var b = new byte[n];
            ulong s = seed * 0x9E3779B97F4A7C15UL + 1;
            for (int i = 0; i < n; i++)
            {
                s = s * 6364136223846793005UL + 1442695040888963407UL;
                b[i] = (byte)(s >> 56);
            }
            return b;
        }

        private static void PutLeU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        private static void PutLeU64(byte[] b, int o, ulong v)
        {
            PutLeU32(b, o, (uint)v); PutLeU32(b, o + 4, (uint)(v >> 32));
        }
    }
}
