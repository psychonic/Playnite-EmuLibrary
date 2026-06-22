using System;
using System.Security.Cryptography;

namespace EmuLibrary.RomTypes.WiiU.Crypto
{
    // Minimal AES-128-CBC (no padding) helper for Wii U content decryption. Exposes both a one-shot decrypt
    // with an explicit IV (used for hash-block content, where every block has its own IV) and a chained
    // transform (used for plain content, which is one continuous CBC stream). All call sites use lengths that
    // are multiples of 16.
    internal sealed class AesCbc : IDisposable
    {
        private readonly Aes _aes;

        public AesCbc(byte[] key)
        {
            _aes = Aes.Create();
            _aes.Key = key;
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.None;
            _aes.BlockSize = 128;
        }

        // One-shot CBC decrypt with a fresh IV; no chaining state is retained between calls.
        public void Decrypt(byte[] iv, byte[] src, int srcOffset, int length, byte[] dst, int dstOffset)
        {
            using (var dec = _aes.CreateDecryptor(_aes.Key, iv))
                dec.TransformBlock(src, srcOffset, length, dst, dstOffset);
        }

        // A CBC decryptor that chains across successive TransformBlock calls, seeded with the given IV.
        public ICryptoTransform CreateChained(byte[] iv) => _aes.CreateDecryptor(_aes.Key, iv);

        public void Dispose() => _aes.Dispose();
    }
}
