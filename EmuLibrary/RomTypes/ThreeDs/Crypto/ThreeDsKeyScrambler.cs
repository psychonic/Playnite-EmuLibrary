using System;

namespace EmuLibrary.RomTypes.ThreeDs.Crypto
{
    // The 3DS AES key scrambler and the 128-bit big-endian arithmetic it needs. A keyslot's normal key is
    // derived from its KeyX and KeyY:
    //
    //   normalKey = ROL128( ( ROL128(KeyX, 2) XOR KeyY ) + generatorConstant , 87 )
    //
    // All operands are 128-bit values held big-endian in 16-byte arrays; the addition is modulo 2^128. This
    // is the same scrambler for the NCCH keyslots (0x2C/0x25/0x18/0x1B) and the ticket common-key slot (0x3D).
    internal static class ThreeDsKeyScrambler
    {
        public static byte[] Derive(byte[] keyX, byte[] keyY, byte[] generatorConstant)
        {
            var t = Rol128(keyX, 2);
            Xor128InPlace(t, keyY);
            Add128InPlace(t, generatorConstant);
            return Rol128(t, 87);
        }

        // Left-rotate a 128-bit big-endian value by `bits` (0..127), returning a new array.
        public static byte[] Rol128(byte[] v, int bits)
        {
            bits &= 127;
            int byteShift = bits / 8;
            int bitShift = bits % 8;
            var shifted = new byte[16];

            // First rotate by whole bytes, then by the remaining bits.
            for (int i = 0; i < 16; i++)
                shifted[i] = v[(i + byteShift) % 16];

            if (bitShift == 0)
                return shifted;

            var result = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                int next = (i + 1) % 16;
                result[i] = (byte)((shifted[i] << bitShift) | (shifted[next] >> (8 - bitShift)));
            }
            return result;
        }

        public static void Xor128InPlace(byte[] target, byte[] other)
        {
            for (int i = 0; i < 16; i++)
                target[i] ^= other[i];
        }

        // target = (target + other) mod 2^128, both big-endian.
        public static void Add128InPlace(byte[] target, byte[] other)
        {
            int carry = 0;
            for (int i = 15; i >= 0; i--)
            {
                int sum = target[i] + other[i] + carry;
                target[i] = (byte)sum;
                carry = sum >> 8;
            }
        }
    }
}
