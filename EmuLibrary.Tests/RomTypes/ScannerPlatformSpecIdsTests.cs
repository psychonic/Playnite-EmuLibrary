using EmuLibrary;
using EmuLibrary.RomTypes;
using EmuLibrary.Util.ScanCache;
using EmuLibrary.Util.ScanConcurrency;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EmuLibrary.Tests.RomTypes
{
    // Pins RomTypeScanner.SupportedPlatformSpecIds per RomType. The settings dropdown (EmulatorMapping.
    // AvailableRomTypes) hides a specialized RomType unless one of these Playnite platform SpecificationIds
    // is served by the chosen emulator profile; the universal SingleFile/MultiFile types declare none and
    // always show. A wrong/renamed spec id would silently hide a type under its correct emulator, so pin
    // the exact strings here. Values are Playnite's canonical Emulation platform ids.
    public class ScannerPlatformSpecIdsTests
    {
        private sealed class StubEmuLibrary : IEmuLibrary
        {
            public ILogger Logger => null;
            public IPlayniteAPI Playnite => null;
            public global::EmuLibrary.Settings.Settings Settings => null;
            public IScanCache ScanCache => null;
            public IScanConcurrency ScanConcurrency => null;
            public global::EmuLibrary.Installs.IInstallProgressService InstallProgress => null;
            public string GetPluginUserDataPath() => null;
            public RomTypeScanner GetScanner(RomType romType) => null;
        }

        // Expected spec ids per RomType. null means "universal" (declares none, always shown). Adding a
        // RomType without adding it here fails EveryRomType_HasAPinnedExpectation below.
        public static readonly Dictionary<RomType, string[]> Expected = new Dictionary<RomType, string[]>
        {
            { RomType.SingleFile, null },
            { RomType.MultiFile, null },
            { RomType.Yuzu, new[] { "nintendo_switch" } },
            { RomType.Ps3, new[] { "sony_playstation3" } },
            { RomType.WiiU, new[] { "nintendo_wiiu" } },
            { RomType.ThreeDs, new[] { "nintendo_3ds" } },
            { RomType.Xbox360, new[] { "xbox360" } },
            { RomType.Psp, new[] { "sony_psp" } },
            { RomType.Vita, new[] { "sony_vita" } },
        };

        private static RomTypeScanner CreateScanner(RomType rt)
        {
            var attr = typeof(RomType).GetField(rt.ToString())
                .GetCustomAttributes(typeof(RomTypeInfoAttribute), false)
                .Cast<RomTypeInfoAttribute>()
                .Single();
            return (RomTypeScanner)Activator.CreateInstance(attr.ScannerType, new StubEmuLibrary());
        }

        [Fact]
        public void EveryRomType_HasAPinnedSpecIdExpectation()
        {
            foreach (RomType rt in Enum.GetValues(typeof(RomType)))
            {
                Assert.True(Expected.ContainsKey(rt),
                    $"RomType.{rt} has no pinned SupportedPlatformSpecIds expectation. Decide which Playnite " +
                    $"platform(s) it targets (or null for a universal type) and add it to {nameof(Expected)}.");

                var actual = CreateScanner(rt).SupportedPlatformSpecIds;
                var expected = Expected[rt];
                if (expected == null)
                {
                    Assert.True(actual == null || actual.Count == 0,
                        $"RomType.{rt} is expected to be universal (no spec ids) but declares some.");
                }
                else
                {
                    Assert.NotNull(actual);
                    Assert.Equal(expected, actual.ToArray());
                }
            }
        }
    }
}
