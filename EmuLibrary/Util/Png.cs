namespace EmuLibrary.Util
{
    // Tiny shared guard for the 8-byte PNG signature. Embedded title icons on several platforms are read out
    // of a raw/decrypted region (PS3 disc/pkg, PSP UMD/PBP, Xbox 360 STFS header / XDBF resource); validating
    // the magic before handing the bytes to Playnite rejects a mislocated or garbage read.
    internal static class Png
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool HasPngSignature(byte[] bytes)
        {
            if (bytes == null || bytes.Length < Signature.Length)
                return false;
            for (int i = 0; i < Signature.Length; i++)
            {
                if (bytes[i] != Signature[i])
                    return false;
            }
            return true;
        }
    }
}
