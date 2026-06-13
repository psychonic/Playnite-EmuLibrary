using System;
using System.Collections.Generic;
using System.Linq;

namespace EmuLibrary.RomTypes
{
    // Picks the "load" file for a game from its set of files. Walks the image extensions in priority
    // order and, for the first extension that has any match, returns the first matching file in the
    // order the files were supplied (e.g. "m3u,cue,bin" returns an m3u/cue before any bin). No implicit
    // sort is applied - input order is the tiebreak, so the caller controls ordering.
    internal static class LoadFileSelector
    {
        // getExtensionLower must return the file's extension, lowercased and without a leading dot
        // (extensionless files return ""). The "<none>" pseudo-extension matches extensionless files.
        public static T Select<T>(IEnumerable<T> files, Func<T, string> getExtensionLower, IEnumerable<string> extensionsInPriorityOrder)
            where T : class
        {
            var fileList = files as IList<T> ?? files.ToList();
            foreach (var ext in extensionsInPriorityOrder)
            {
                var match = fileList.FirstOrDefault(f => Matches(getExtensionLower(f), ext));
                if (match != null)
                    return match;
            }
            return null;
        }

        private static bool Matches(string fileExtensionLower, string targetExtensionLower)
        {
            return fileExtensionLower == targetExtensionLower
                || (fileExtensionLower == "" && targetExtensionLower == "<none>");
        }
    }
}
