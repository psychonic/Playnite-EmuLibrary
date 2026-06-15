using EmuLibrary.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;

namespace EmuLibrary.RomTypes.Ps3
{
    // Resolves RPCS3's virtual file system paths from the mapped emulator install dir.
    //
    // dev_hdd0 defaults to "<EmulatorBasePathResolved>/dev_hdd0" but is relocatable via RPCS3's
    // Manage → Virtual File System (persisted in vfs.yml). Resolution checks vfs.yml next to the
    // exe and in a config/ subdirectory before falling back to the default location.
    internal static class Rpcs3Emulator
    {
        private static readonly IDeserializer _yamlDeserializer = new DeserializerBuilder().Build();

        public static string GetDevHdd0(EmulatorMapping mapping)
        {
            return GetDevHdd0(mapping?.EmulatorBasePathResolved);
        }

        // Internal overload for testability — takes the emulator base path directly.
        internal static string GetDevHdd0(string emulatorBasePath)
        {
            if (string.IsNullOrEmpty(emulatorBasePath))
                return null;
            var vfs = TryReadDevHdd0FromVfsYml(emulatorBasePath);
            return vfs ?? Path.Combine(emulatorBasePath, "dev_hdd0");
        }

        public static string GetGameDir(EmulatorMapping mapping, string titleId)
        {
            var devHdd0 = GetDevHdd0(mapping);
            return devHdd0 == null ? null : Path.Combine(devHdd0, "game", titleId ?? "");
        }

        // RPCS3 stores RAP licenses for the default user (00000001) here.
        public static string GetExdataDir(EmulatorMapping mapping)
        {
            var devHdd0 = GetDevHdd0(mapping);
            return devHdd0 == null ? null : Path.Combine(devHdd0, "home", "00000001", "exdata");
        }

        private static string TryReadDevHdd0FromVfsYml(string emulatorBasePath)
        {
            var candidates = new[]
            {
                Path.Combine(emulatorBasePath, "vfs.yml"),
                Path.Combine(emulatorBasePath, "config", "vfs.yml"),
            };
            foreach (var file in candidates)
            {
                if (!File.Exists(file)) continue;
                try
                {
                    var result = ParseDevHdd0FromVfsYml(File.ReadAllText(file));
                    if (result != null) return result;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        // Parses a vfs.yml YAML string, returning the /dev_hdd0/ path if present and non-empty,
        // or null if the entry is absent or set to the default empty string.
        internal static string ParseDevHdd0FromVfsYml(string yamlContent)
        {
            if (string.IsNullOrWhiteSpace(yamlContent))
                return null;
            try
            {
                var vfs = _yamlDeserializer.Deserialize<Dictionary<string, string>>(yamlContent);
                if (vfs == null) return null;
                // RPCS3 uses "/dev_hdd0/" as the key (with leading and trailing slash).
                if (vfs.TryGetValue("/dev_hdd0/", out var path) && !string.IsNullOrEmpty(path))
                    return path;
            }
            catch (Exception) { }
            return null;
        }
    }
}
