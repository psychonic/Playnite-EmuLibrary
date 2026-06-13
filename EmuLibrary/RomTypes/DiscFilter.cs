using System.Text.RegularExpressions;

namespace EmuLibrary.RomTypes
{
    // Shared by the SingleFile and MultiFile scanners to skip multi-disc entries. Such games are
    // expected to be handled as a single multi-file/m3u entry rather than scanned per-disc, so any
    // file/folder name carrying a "(Disc N)" / "(Disk N)" marker is excluded from per-item scanning.
    internal static class DiscFilter
    {
        private static readonly Regex s_discMarkerPattern = new Regex(@"\((?:Disc|Disk) \d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // True if the name carries a "(Disc N)" / "(Disk N)" marker and should be excluded from scanning.
        public static bool IsExcludedDisc(string name) => s_discMarkerPattern.IsMatch(name);
    }
}
