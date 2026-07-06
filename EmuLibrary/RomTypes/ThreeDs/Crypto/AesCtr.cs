using System;
using System.Security.Cryptography;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // AES-128 in counter (CTR) mode. .NET Framework's Aes only offers CBC/ECB, so CTR is built by hand:
    // encrypt successive 16-byte counter blocks with AES-ECB and XOR the keystream over the data. CTR is
    // symmetric, so the same routine both decrypts (NCCH sections) and is used wherever 3DS uses CTR.
    //
    // The counter is a big-endian 128-bit value that increments by one per 16-byte block. A region can be
    // decrypted starting from an arbitrary block by seeding the counter via Add (offset / 16).
    internal sealed class AesCtr : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb;

        public AesCtr(byte[] key)
        {
            _aes = Aes.Create();
            _aes.Key = key;
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.BlockSize = 128;
            _ecb = _aes.CreateEncryptor();
        }

        // The keystream is generated in batches of this many 16-byte blocks per AES-ECB call. Encrypting one
        // counter block at a time makes every 16 bytes a separate ICryptoTransform call, whose per-call overhead
        // (and defeat of AES-NI batching) dominates — a multi-GB RomFS then takes many minutes. Transforming a
        // 64 KiB block of counters at once amortizes that away.
        private const int BatchBlocks = 4096; // 64 KiB keystream per ECB call

        // Decrypt/encrypt `length` bytes in place starting at `offset`, with the running counter `counter`
        // (16-byte big-endian). `length` should be a multiple of 16 except on the final chunk of a region, so
        // successive calls continue the same keystream — the counter is advanced in place as blocks are
        // consumed, letting a large region be processed in chunks.
        public void Apply(byte[] counter, byte[] buffer, int offset, int length)
        {
            if (length <= 0)
                return;

            int batchBytes = Math.Min(BatchBlocks, (length + 15) / 16) * 16;
            var counters = new byte[batchBytes];
            var keystream = new byte[batchBytes];

            int pos = 0;
            while (pos < length)
            {
                // Fill this batch's counter blocks (advancing the running counter once per block), then produce
                // the whole keystream in a single AES-ECB transform.
                int blocks = Math.Min(BatchBlocks, (length - pos + 15) / 16);
                for (int b = 0; b < blocks; b++)
                {
                    Buffer.BlockCopy(counter, 0, counters, b * 16, 16);
                    Increment(counter);
                }
                _ecb.TransformBlock(counters, 0, blocks * 16, keystream, 0);

                int n = Math.Min(blocks * 16, length - pos);
                for (int i = 0; i < n; i++)
                    buffer[offset + pos + i] ^= keystream[i];
                pos += n;
            }
        }

        // counter += blocks (16-byte big-endian add). Used to position the counter at a region's start.
        public static void Add(byte[] counter, ulong blocks)
        {
            ulong carry = blocks;
            for (int j = 15; j >= 0 && carry != 0; j--)
            {
                ulong v = (ulong)counter[j] + (byte)(carry & 0xFF);
                carry >>= 8;
                carry += v >> 8;
                counter[j] = (byte)v;
            }
        }

        private static void Increment(byte[] ctr)
        {
            for (int i = 15; i >= 0; i--)
            {
                if (++ctr[i] != 0)
                    break;
            }
        }

        public void Dispose()
        {
            _ecb.Dispose();
            _aes.Dispose();
        }
    }
}
