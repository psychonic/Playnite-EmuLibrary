using Playnite.SDK;

namespace EmuLibrary.Util
{
    // Pure helper for resolving stored paths against a (possibly portable) Playnite install.
    // A portable Playnite stores paths with the {PlayniteDir} expandable variable in place of the
    // install location so the library is relocatable; a non-portable install stores absolute paths.
    internal static class PortablePathResolver
    {
        // Expands the {PlayniteDir} variable in a stored path to the actual application path when the
        // install is portable; otherwise returns the path unchanged. Null in -> null out.
        public static string Resolve(string path, bool isPortable, string applicationPath)
        {
            return isPortable ? path?.Replace(ExpandableVariables.PlayniteDirectory, applicationPath) : path;
        }
    }
}
