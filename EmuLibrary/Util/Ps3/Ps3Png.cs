namespace EmuLibrary.Util.Ps3
{
    // Tiny shared guard: PS3 ICON0.PNG icons are read out of the disc's unencrypted region (Ps3Iso) or the
    // pkg's decrypted data segment (Ps3Pkg); validating the 8-byte PNG signature before handing the bytes to
    // Playnite rejects a mislocated/garbage read (mirrors StfsPackage's PngSignature guard).
    internal static class Ps3Png
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
