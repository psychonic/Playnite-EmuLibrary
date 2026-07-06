using EmuLibrary.Util.ZArchive;
using System;
using System.IO;

namespace EmuLibrary.RomTypes.Xbox360
{
    // Reads a title's identity out of a Xenia .zar — a ZArchive (Exzap's Cemu container, zstd-compressed) of an
    // extracted, decrypted Xbox 360 dump. Xenia mounts the archive at the volume root and boots \default.xex, so
    // the execution id lives in the archive's top-level default.xex: the same plaintext XEX2 header the disc path
    // reads (see XexHeader), just sourced from inside the archive. Only the header region is needed, so we inflate
    // just a prefix of default.xex rather than the whole executable.
    internal static class Xbox360ZarReader
    {
        // Xenia's zar VFS looks up "default.xex" at the archive root; a zar without one wouldn't boot there either.
        public const string DefaultXexPath = "default.xex";

        // Reads up to maxBytes of the archive's root default.xex. False if the file isn't a valid ZArchive or has
        // no top-level default.xex.
        public static bool TryReadDefaultXex(string zarPath, int maxBytes, out byte[] xex)
        {
            xex = null;
            try
            {
                using (var reader = ZArchiveReader.Open(zarPath))
                    return reader.TryReadFilePrefix(DefaultXexPath, maxBytes, out xex);
            }
            catch (InvalidDataException)
            {
                // Not a ZArchive (bad footer/magic/version) — treat as "not a zar", not an error.
                return false;
            }
        }

        // Stream overload (used by tests). Leaves the stream open for the caller.
        public static bool TryReadDefaultXex(Stream zarStream, int maxBytes, out byte[] xex)
        {
            xex = null;
            try
            {
                using (var reader = ZArchiveReader.Open(zarStream, leaveOpen: true))
                    return reader.TryReadFilePrefix(DefaultXexPath, maxBytes, out xex);
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }
}
