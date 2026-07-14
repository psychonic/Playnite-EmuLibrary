using EmuLibrary.Util.Vita;
using System.Security.Cryptography;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Vita
{
    // Vita inner-PFS (gamedata) crypto coverage. Network-free, deterministic:
    //  * the ICV `secret` derivation (F00D contract key -> AES-CBC-CTS -> HMAC-SHA1) is pinned to a vector that
    //    was verified against a REAL Sony package's files.db header_icv (HMAC-SHA1(secret, header) == stored icv),
    //    so it locks the whole key-derivation chain (F00DDeriveKey + ComputeSecret) end to end.
    //  * the per-sector cipher (per-sector tweak-as-IV + AES-CBC + ciphertext-stealing tail) round-trips.
    public class VitaPfsTests
    {
        // Real title klicensee (36 Fragments of Midnight, UP0891-PCSE01169) — a PUBLIC value from its zRIF.
        private static readonly byte[] Klicensee =
        {
            0x17, 0x66, 0x5A, 0x19, 0x8A, 0x5A, 0xE8, 0xDE,
            0xF6, 0x60, 0xBF, 0x84, 0x6B, 0x6D, 0x7F, 0x24,
        };

        [Fact]
        public void DeriveSecret_MatchesVectorVerifiedAgainstRealFilesDbHeaderIcv()
        {
            // files_salt from that title's files.db; icv_salt 0 is what files.db uses for its header signature.
            const uint filesSalt = 2479423894u;
            var secret = VitaPfs.DeriveSecret(Klicensee, filesSalt, 0);

            var expected = new byte[]
            {
                0xD6, 0x46, 0x60, 0x9D, 0x48, 0x5E, 0x90, 0xEF, 0xBB, 0xC1,
                0x32, 0xEA, 0x53, 0x56, 0x89, 0x9F, 0x0D, 0xF9, 0xB9, 0x04,
            };
            Assert.Equal(expected, secret);
        }

        [Fact]
        public void DecryptSectors_RoundTripsMultiSectorWithCtsTail()
        {
            // 0x8000 (one full sector) + 40 bytes (a second sector whose 40-byte tail is not 16-aligned -> CTS).
            var plain = new byte[0x8000 + 40];
            var rng = new System.Random(1234);
            rng.NextBytes(plain);

            var dataKey = new byte[16];
            var tweakEncKey = new byte[16];
            for (int i = 0; i < 16; i++) { dataKey[i] = (byte)(i + 1); tweakEncKey[i] = (byte)(0x30 + i); }

            var cipher = ReferenceEncrypt(plain, dataKey, tweakEncKey);
            Assert.NotEqual(plain, cipher); // sanity: it really is encrypted

            var decrypted = VitaPfs.DecryptSectorsForTest(cipher, dataKey, tweakEncKey);
            Assert.Equal(plain, decrypted);
        }

        // Reference encrypt matching VitaPfs.DecryptBuffer: per 0x8000 sector, IV = LE64(sector*0x8000) ^ tweak
        // key; AES-CBC over the aligned portion; ciphertext-stealing (P_tail ^ ECB_enc(lastCipherBlock)) tail.
        private static byte[] ReferenceEncrypt(byte[] plain, byte[] dataKey, byte[] tweakEncKey)
        {
            const int sectorSize = 0x8000;
            var outBuf = (byte[])plain.Clone();
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = dataKey;
                using (var ecb = aes.CreateEncryptor())
                {
                    int offset = 0;
                    long sector = 0;
                    while (offset < outBuf.Length)
                    {
                        int sectorLen = System.Math.Min(sectorSize, outBuf.Length - offset);
                        long byteOffset = sector * sectorSize;
                        var iv = new byte[16];
                        for (int i = 0; i < 8; i++) iv[i] = (byte)(byteOffset >> (8 * i));
                        for (int i = 0; i < 16; i++) iv[i] ^= tweakEncKey[i];

                        int sizeBlock = sectorLen & ~0xF;
                        int sizeTail = sectorLen & 0xF;

                        var prev = new byte[16];
                        System.Array.Copy(iv, prev, 16);
                        var tmp = new byte[16];
                        for (int b = 0; b < sizeBlock; b += 16)
                        {
                            for (int i = 0; i < 16; i++) tmp[i] = (byte)(outBuf[offset + b + i] ^ prev[i]);
                            ecb.TransformBlock(tmp, 0, 16, tmp, 0);
                            for (int i = 0; i < 16; i++) { outBuf[offset + b + i] = tmp[i]; prev[i] = tmp[i]; }
                        }
                        if (sizeTail != 0)
                        {
                            var tw = new byte[16];
                            ecb.TransformBlock(prev, 0, 16, tw, 0);
                            for (int i = 0; i < sizeTail; i++) outBuf[offset + sizeBlock + i] ^= tw[i];
                        }
                        offset += sectorLen;
                        sector++;
                    }
                }
            }
            return outBuf;
        }
    }
}
