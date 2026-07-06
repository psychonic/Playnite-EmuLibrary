using EmuLibrary.RomTypes.ThreeDs.Crypto;
using Playnite.SDK;
using System;
using System.IO;
using System.Threading;

namespace EmuLibrary.RomTypes.ThreeDs
{
    // Loose 3DS images (.cci/.3ds cartridge dumps, .cxi executables) are installed by a plain copy, but a dump
    // may be either already-decrypted (boots directly) or NCCH-encrypted (Azahar's NCCH loader rejects encrypted
    // NCCH). To make both cases work without keys at boot, an encrypted copy is decrypted in place at the
    // destination after copying: every NCCH partition is brought to no_crypto via the same decryptor used for
    // CIA content. Already-decrypted files and non-NCCH shapes (.3dsx homebrew, Z3DS .z* variants) are untouched.
    internal static class ThreeDsLooseDecrypt
    {
        // True if `path` is a 3DS image with at least one encrypted NCCH partition (so it needs keys/decryption).
        public static bool NeedsDecryption(string path)
        {
            var ext = Path.GetExtension(path);
            if (!IsDecryptableExtension(ext))
                return false;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    foreach (var ncchBase in NcchBases(fs, ext))
                    {
                        if (NcchDecryptor.IsEncryptedNcch(fs, ncchBase))
                            return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        // Decrypts every encrypted NCCH partition of `path` in place. No-op for already-decrypted / non-NCCH
        // files. Returns true if any partition was decrypted.
        public static bool DecryptInPlace(string path, ThreeDsKeys keys, ILogger logger, CancellationToken ct)
        {
            var ext = Path.GetExtension(path);
            if (!IsDecryptableExtension(ext))
                return false;

            bool any = false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var ncchBase in NcchBases(fs, ext))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!NcchDecryptor.IsEncryptedNcch(fs, ncchBase))
                        continue;
                    NcchDecryptor.Decrypt(fs, keys, ct, ncchBase);
                    any = true;
                    logger?.Info($"[3DS] Decrypted NCCH partition at 0x{ncchBase:X} in \"{Path.GetFileName(path)}\".");
                }
            }
            return any;
        }

        private static bool IsDecryptableExtension(string ext) =>
            ext.Equals(".cci", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".3ds", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cxi", StringComparison.OrdinalIgnoreCase);

        // The NCCH base offsets to consider: NCSD cartridge images (.cci/.3ds) expose their partition table;
        // a bare .cxi is a single NCCH at offset 0. (Some .3ds dumps are stored as a bare NCCH rather than an
        // NCSD — fall back to offset 0 when the NCSD magic is absent.)
        private static System.Collections.Generic.IEnumerable<long> NcchBases(FileStream fs, string ext)
        {
            if ((ext.Equals(".cci", StringComparison.OrdinalIgnoreCase)
                 || ext.Equals(".3ds", StringComparison.OrdinalIgnoreCase))
                && Ncsd.IsNcsd(fs))
            {
                foreach (var p in Ncsd.ReadPartitions(fs))
                    yield return p.Offset;
            }
            else
            {
                yield return 0;
            }
        }
    }
}
