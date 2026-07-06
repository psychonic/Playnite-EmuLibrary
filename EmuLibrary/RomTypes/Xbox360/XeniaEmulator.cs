using EmuLibrary.Settings;
using System;
using System.IO;

namespace EmuLibrary.RomTypes.Xbox360
{
    // Resolves Xenia's content tree from the mapped emulator install dir. Fork-agnostic (canary = master):
    // the content layout is identical across forks.
    //
    //   content root = portable? {XeniaExeDir}\content        (if portable.txt sits beside xenia.exe)
    //                : %USERPROFILE%\Documents\Xenia\content
    //
    // Updates/DLC for non-save content live under a fixed XUID of all zeros:
    //   {contentRoot}\0000000000000000\{TitleId:X8}\{ContentType:X8}\{ContentName}
    internal static class XeniaEmulator
    {
        // The XUID used for non-save content (installers / updates / DLC).
        public const string NonSaveXuid = "0000000000000000";

        public static string GetContentRoot(EmulatorMapping mapping)
        {
            return GetContentRoot(mapping?.EmulatorBasePathResolved);
        }

        // Internal overload for testability — takes the emulator base path directly.
        internal static string GetContentRoot(string emulatorBasePath)
        {
            if (!string.IsNullOrEmpty(emulatorBasePath) &&
                File.Exists(Path.Combine(emulatorBasePath, "portable.txt")))
            {
                return Path.Combine(emulatorBasePath, "content");
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
                return null;

            return Path.Combine(documents, "Xenia", "content");
        }

        // The directory a content unit of the given type is written to under the content tree. Pure (no
        // filesystem access) so it's directly unit-testable.
        public static string GetContentTypeDir(string contentRoot, uint titleId, uint contentType)
        {
            return Path.Combine(contentRoot, NonSaveXuid, titleId.ToString("X8"), contentType.ToString("X8"));
        }

        // Resolved destination directory for an update/DLC unit, or null if the content root can't be resolved.
        public static string GetContentTypeDir(EmulatorMapping mapping, uint titleId, uint contentType)
        {
            var root = GetContentRoot(mapping);
            return root == null ? null : GetContentTypeDir(root, titleId, contentType);
        }
    }
}
