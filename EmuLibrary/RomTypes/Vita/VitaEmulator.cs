using EmuLibrary.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;

namespace EmuLibrary.RomTypes.Vita
{
    // Resolves Vita3K's ux0 filesystem root from the mapped emulator install dir, the way Rpcs3Emulator
    // resolves dev_hdd0 from vfs.yml.
    //
    // Vita3K keeps a config.yml next to Vita3K.exe with a "pref-path:" key; the emulated storage tree is
    // "<pref-path>/ux0". When pref-path is unset, Vita3K's own default is %APPDATA%\Vita3K\Vita3K, but since
    // the mapping already points at the emulator install dir we fall back to "<EmulatorBasePathResolved>/ux0"
    // (matching Rpcs3Emulator's "<base>/dev_hdd0" fallback). [VERIFY] confirmed 2026-07: config.yml lives
    // beside the exe, the key is "pref-path", and games install under "<pref-path>/ux0/app".
    internal static class VitaEmulator
    {
        private static readonly IDeserializer _yamlDeserializer = new DeserializerBuilder().Build();

        public static string GetUx0(EmulatorMapping mapping)
        {
            return GetUx0(mapping?.EmulatorBasePathResolved);
        }

        // Internal overload for testability — takes the emulator base path directly.
        internal static string GetUx0(string emulatorBasePath)
        {
            if (string.IsNullOrEmpty(emulatorBasePath))
                return null;

            var prefPath = TryReadPrefPathFromConfigYml(emulatorBasePath);
            return string.IsNullOrEmpty(prefPath)
                ? Path.Combine(emulatorBasePath, "ux0")
                : Path.Combine(prefPath, "ux0");
        }

        // Base game destination: ux0/app/<titleId>.
        public static string GetAppDir(EmulatorMapping mapping, string titleId)
        {
            var ux0 = GetUx0(mapping);
            return ux0 == null ? null : Path.Combine(ux0, "app", titleId ?? "");
        }

        // Update destination: ux0/patch/<titleId>. Vita3K applies the patch tree over the app at launch.
        public static string GetPatchDir(EmulatorMapping mapping, string titleId)
        {
            var ux0 = GetUx0(mapping);
            return ux0 == null ? null : Path.Combine(ux0, "patch", titleId ?? "");
        }

        // DLC destination base: ux0/addcont/<titleId>.
        public static string GetAddContDir(EmulatorMapping mapping, string titleId)
        {
            var ux0 = GetUx0(mapping);
            return ux0 == null ? null : Path.Combine(ux0, "addcont", titleId ?? "");
        }

        // License destination base: ux0/license/<titleId>.
        //
        // NOTE: Vita3K flattens the license tree — unlike a real Vita (ux0:license/app/<id>/...), its
        // get_license()/copy_license() read and write "<ux0>/license/<TITLE_ID>/<CONTENT_ID>.rif" with NO
        // "app" component (vita3k/packages/src/license.cpp). Writing under an extra "app/" dir makes Vita3K's
        // open_license() miss the file, leaving the klicensee zero ("No klic provided for encrypted App") so the
        // retail eboot never decrypts. Match Vita3K's actual path.
        public static string GetLicenseDir(EmulatorMapping mapping, string titleId)
        {
            var ux0 = GetUx0(mapping);
            return ux0 == null ? null : Path.Combine(ux0, "license", titleId ?? "");
        }

        private static string TryReadPrefPathFromConfigYml(string emulatorBasePath)
        {
            var candidates = new[]
            {
                Path.Combine(emulatorBasePath, "config.yml"),
                Path.Combine(emulatorBasePath, "config", "config.yml"),
            };
            foreach (var file in candidates)
            {
                if (!File.Exists(file)) continue;
                try
                {
                    var result = ParsePrefPathFromConfigYml(File.ReadAllText(file));
                    if (result != null) return result;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        // Parses a Vita3K config.yml string, returning the "pref-path" value if present and non-empty, else
        // null. config.yml is a flat YAML mapping of mixed scalar types, so deserialize to object values and
        // read pref-path as a string.
        internal static string ParsePrefPathFromConfigYml(string yamlContent)
        {
            if (string.IsNullOrWhiteSpace(yamlContent))
                return null;
            try
            {
                var cfg = _yamlDeserializer.Deserialize<Dictionary<string, object>>(yamlContent);
                if (cfg == null) return null;
                if (cfg.TryGetValue("pref-path", out var value) && value != null)
                {
                    var s = value.ToString();
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
