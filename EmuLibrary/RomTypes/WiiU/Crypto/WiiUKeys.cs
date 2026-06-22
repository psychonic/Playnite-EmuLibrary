using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EmuLibrary.RomTypes.WiiU.Crypto
{
    // Wii U key material. The Wii U common key (the AES key that decrypts a ticket's title key) is not
    // shipped with the plugin; it is read from Cemu's keys.txt, exactly as the old CemuLibrary did, and
    // verified by MD5 so a wrong/missing key fails loudly rather than producing garbage.
    internal static class WiiUKeys
    {
        // MD5 of the uppercase hex common key, used to validate keys.txt without embedding the key itself.
        private static readonly byte[] CommonKeyMd5 =
            { 0x35, 0xAC, 0x59, 0x94, 0x97, 0x22, 0x79, 0x33, 0x1D, 0x97, 0x09, 0x4F, 0xA2, 0xFB, 0x97, 0xFC };

        // Loads and validates the Wii U common key from Cemu's keys.txt (first non-comment line).
        public static byte[] LoadCommonKey(string keysTxtPath)
        {
            if (!File.Exists(keysTxtPath))
                throw new FileNotFoundException($"Could not find Cemu keys file at \"{keysTxtPath}\".", keysTxtPath);

            var firstLine = File.ReadLines(keysTxtPath).First();
            firstLine = Regex.Replace(firstLine, @"\#.*$", "").Trim().ToUpperInvariant();

            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.ASCII.GetBytes(firstLine));
                if (!hash.SequenceEqual(CommonKeyMd5))
                    throw new Exception("Wii U common key in keys.txt is invalid.");
            }

            return HexToBytes(firstLine);
        }

        public static byte[] HexToBytes(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have an even length.", nameof(hex));
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
