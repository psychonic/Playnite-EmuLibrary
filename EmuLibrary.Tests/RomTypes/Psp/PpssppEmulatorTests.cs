using EmuLibrary.RomTypes.Psp;
using System;
using System.IO;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Psp
{
    // Pins PPSSPP's two-backend memory-stick resolution (WS12 §6c): standalone PPSSPP's portable /
    // memstick_dir.txt / Documents fallback order, and the RetroArch PPSSPP-core system_directory path.
    public class PpssppEmulatorTests
    {
        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eltest-psp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Standalone_PortableMemstick_Wins()
        {
            var dir = NewTempDir();
            try
            {
                var memstick = Path.Combine(dir, "memstick");
                Directory.CreateDirectory(memstick);
                // A memstick_dir.txt is also present, but the portable memstick\ folder takes precedence.
                File.WriteAllText(Path.Combine(dir, "memstick_dir.txt"), @"C:\ignored");

                Assert.Equal(memstick, PpssppEmulator.GetMemstickRoot(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Standalone_MemstickDirTxt_UsedWhenNoPortableFolder()
        {
            var dir = NewTempDir();
            try
            {
                var target = Path.Combine(dir, "ms-elsewhere");
                File.WriteAllText(Path.Combine(dir, "memstick_dir.txt"), target + Environment.NewLine);

                Assert.Equal(target, PpssppEmulator.GetMemstickRoot(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Standalone_FallsBackToDocuments()
        {
            var dir = NewTempDir();
            try
            {
                var expected = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PPSSPP");
                Assert.Equal(expected, PpssppEmulator.GetMemstickRoot(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void RetroArch_DetectedByFolderName_UsesSystemDirPpsspp()
        {
            var parent = NewTempDir();
            try
            {
                // A dir whose path contains "retroarch" is treated as the RetroArch backend.
                var ra = Path.Combine(parent, "RetroArch");
                Directory.CreateDirectory(ra);
                File.WriteAllText(Path.Combine(ra, "retroarch.cfg"),
                    "system_directory = \":\\system\"\n");

                Assert.True(PpssppEmulator.IsRetroArch(ra));
                Assert.Equal(Path.Combine(ra, "system", "PPSSPP"), PpssppEmulator.GetMemstickRoot(ra));
            }
            finally { Directory.Delete(parent, true); }
        }

        [Fact]
        public void RetroArch_AbsoluteSystemDirectory_Honored()
        {
            var absSystem = ParseSystem("system_directory = \"D:\\ra\\system\"");
            Assert.Equal(@"D:\ra\system", absSystem);
        }

        [Fact]
        public void RetroArch_ColonRelative_ResolvesAgainstBase()
        {
            Assert.Equal(Path.Combine(@"C:\ra", "system"),
                PpssppEmulator.ParseSystemDirectory("system_directory = \":\\system\"", @"C:\ra"));
        }

        [Fact]
        public void RetroArch_DefaultKeyword_ResolvesToBaseSystem()
        {
            Assert.Equal(Path.Combine(@"C:\ra", "system"),
                PpssppEmulator.ParseSystemDirectory("system_directory = \"default\"", @"C:\ra"));
        }

        [Fact]
        public void RetroArch_NoCfg_DefaultsToBaseSystem()
        {
            var parent = NewTempDir();
            try
            {
                var ra = Path.Combine(parent, "retroarch");
                Directory.CreateDirectory(ra);
                Assert.Equal(Path.Combine(ra, "system"), PpssppEmulator.GetRetroArchSystemDir(ra));
            }
            finally { Directory.Delete(parent, true); }
        }

        private static string ParseSystem(string line) =>
            PpssppEmulator.ParseSystemDirectory(line, @"C:\ra");
    }
}
