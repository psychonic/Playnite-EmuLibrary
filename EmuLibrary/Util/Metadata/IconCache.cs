using Playnite.SDK;
using System;
using System.IO;

namespace EmuLibrary.Util.Metadata
{
    // Extracts a format-embedded icon once to a stable per-key file under the plugin's data dir and returns its
    // path, so re-scans reuse the file instead of re-extracting. Writes atomically (temp file + File.Move) so
    // concurrent scan workers can't tear a half-written file, and swallows the losing-writer race. Fail-soft:
    // any error, or an extract() that yields null (source has no icon), returns null. Shared by every scanner
    // that pulls an icon out of a ROM/package (X360 STFS thumbnail, Switch/PS3/Wii U embedded icons).
    internal static class IconCache
    {
        // Returns the path to a cached icon for `key` under metadata/<subDir>/<key>.<ext>, extracting once via
        // `extract` (which returns raw image bytes, or null when the source has no icon). `ext` is
        // "png" | "jpg" | "bmp" — a format Playnite/WPF renders natively.
        public static string EnsureIcon(string pluginUserDataPath, string subDir, string key, string ext,
                                        Func<byte[]> extract, ILogger logger)
        {
            try
            {
                var dir = Path.Combine(pluginUserDataPath, "metadata", subDir);
                var dest = Path.Combine(dir, key + "." + ext);
                if (File.Exists(dest))
                    return dest;

                var bytes = extract();
                if (bytes == null || bytes.Length == 0)
                    return null;

                Directory.CreateDirectory(dir);
                var tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                try
                {
                    File.Move(tmp, dest);
                }
                catch (IOException)
                {
                    // Another worker wrote the same key first; reuse it.
                    try { File.Delete(tmp); } catch { /* best effort */ }
                }
                return File.Exists(dest) ? dest : null;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[IconCache] Failed to extract icon \"{subDir}/{key}.{ext}\".");
                return null;
            }
        }
    }
}
