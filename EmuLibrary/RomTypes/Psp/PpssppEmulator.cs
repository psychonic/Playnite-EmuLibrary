using EmuLibrary.Settings;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace EmuLibrary.RomTypes.Psp
{
    // Resolves PPSSPP's memory-stick root from the mapped emulator install dir. Two backends, keyed off the
    // emulator identity:
    //
    //   Standalone PPSSPP: replicate PPSSPP's own order —
    //     (1) <exeDir>\memstick\           exists  => portable install, that's the root
    //     (2) <exeDir>\memstick_dir.txt    exists  => the root is the path stored in it
    //     (3) else                                 => %USERPROFILE%\Documents\PPSSPP
    //
    //   RetroArch PPSSPP core:
    //     root = <systemDir>\PPSSPP, where systemDir is `system_directory` from retroarch.cfg
    //            (default <retroarch>\system). RetroArch writes ":\..." for a path relative to its own dir.
    //
    // Content installs under <root>\PSP\GAME\<titleId> (and \PSP\SAVEDATA\<name> when routed). If the root
    // can't be resolved the install fails with a clear message rather than guessing (WS12 §6c — no override).
    internal static class PpssppEmulator
    {
        public static string GetMemstickRoot(EmulatorMapping mapping)
        {
            return GetMemstickRoot(mapping?.EmulatorBasePathResolved);
        }

        // Internal overload for testability — takes the emulator base path directly.
        internal static string GetMemstickRoot(string emulatorBasePath)
        {
            return IsRetroArch(emulatorBasePath)
                ? GetRetroArchMemstickRoot(emulatorBasePath)
                : GetStandaloneMemstickRoot(emulatorBasePath);
        }

        // GAME subtree destination for a title's update/DLC payload folder.
        public static string GetGameDir(EmulatorMapping mapping, string titleId)
        {
            var root = GetMemstickRoot(mapping);
            return root == null ? null : Path.Combine(root, "PSP", "GAME", titleId ?? "");
        }

        // SAVEDATA subtree destination (defensive; unused for the current all-GAME content).
        public static string GetSavedataDir(EmulatorMapping mapping, string name)
        {
            var root = GetMemstickRoot(mapping);
            return root == null ? null : Path.Combine(root, "PSP", "SAVEDATA", name ?? "");
        }

        // Destination directory for a content item, honoring its GAME/SAVEDATA route.
        public static string GetContentDir(EmulatorMapping mapping, PspContentRef item)
        {
            return item.Route == PspRoute.Savedata
                ? GetSavedataDir(mapping, item.DestSubPath)
                : GetGameDir(mapping, item.DestSubPath);
        }

        // A RetroArch install is identified by its exe/dir naming — RetroArch's PPSSPP is a libretro core, not a
        // standalone exe, so the emulator points at RetroArch itself.
        internal static bool IsRetroArch(string emulatorBasePath)
        {
            if (string.IsNullOrEmpty(emulatorBasePath))
                return false;
            if (emulatorBasePath.IndexOf("retroarch", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            try
            {
                return File.Exists(Path.Combine(emulatorBasePath, "retroarch.exe"));
            }
            catch
            {
                return false;
            }
        }

        internal static string GetStandaloneMemstickRoot(string emulatorBasePath)
        {
            if (!string.IsNullOrEmpty(emulatorBasePath))
            {
                var portable = Path.Combine(emulatorBasePath, "memstick");
                if (Directory.Exists(portable))
                    return portable;

                var dirTxt = Path.Combine(emulatorBasePath, "memstick_dir.txt");
                if (File.Exists(dirTxt))
                {
                    try
                    {
                        var stored = File.ReadAllText(dirTxt).Trim();
                        if (!string.IsNullOrEmpty(stored))
                            return stored;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
                return null;
            return Path.Combine(documents, "PPSSPP");
        }

        internal static string GetRetroArchMemstickRoot(string emulatorBasePath)
        {
            var systemDir = GetRetroArchSystemDir(emulatorBasePath);
            return systemDir == null ? null : Path.Combine(systemDir, "PPSSPP");
        }

        internal static string GetRetroArchSystemDir(string emulatorBasePath)
        {
            if (string.IsNullOrEmpty(emulatorBasePath))
                return null;

            var cfg = Path.Combine(emulatorBasePath, "retroarch.cfg");
            if (File.Exists(cfg))
            {
                try
                {
                    var parsed = ParseSystemDirectory(File.ReadAllText(cfg), emulatorBasePath);
                    if (!string.IsNullOrEmpty(parsed))
                        return parsed;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            // RetroArch's default when unset.
            return Path.Combine(emulatorBasePath, "system");
        }

        // Parses a `system_directory = "..."` line out of retroarch.cfg. A value of "default" or a leading ":"
        // (RetroArch's "relative to the RetroArch dir" marker) resolves against emulatorBasePath.
        internal static string ParseSystemDirectory(string cfgContent, string emulatorBasePath)
        {
            if (string.IsNullOrEmpty(cfgContent))
                return null;

            var m = Regex.Match(cfgContent, @"(?m)^\s*system_directory\s*=\s*""?(?<val>[^""\r\n]*)""?\s*$");
            if (!m.Success)
                return null;

            var val = m.Groups["val"].Value.Trim();
            if (string.IsNullOrEmpty(val) || string.Equals(val, "default", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(emulatorBasePath) ? null : Path.Combine(emulatorBasePath, "system");

            // ":" or ":\foo" — relative to the RetroArch directory.
            if (val[0] == ':')
            {
                var rel = val.Substring(1).TrimStart('\\', '/');
                return string.IsNullOrEmpty(emulatorBasePath)
                    ? null
                    : (rel.Length == 0 ? emulatorBasePath : Path.Combine(emulatorBasePath, rel));
            }

            return val;
        }
    }
}
