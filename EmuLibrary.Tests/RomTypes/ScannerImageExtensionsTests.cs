using EmuLibrary;
using EmuLibrary.RomTypes;
using EmuLibrary.Util.ScanCache;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EmuLibrary.Tests.RomTypes
{
    // Pins RomTypeScanner.RequiresProfileImageExtensions per RomType. Settings.VerifySettings only
    // enforces "profile must declare image extensions" when this is true; the SingleFile/MultiFile
    // scanners enumerate the source by those extensions, while Yuzu/PS3 scan by their own hardcoded
    // format logic (and RPCS3's built-in profile declares no extensions). If someone re-tightens the
    // check or flips a scanner, this fails loudly.
    public class ScannerImageExtensionsTests
    {
        // Minimal IEmuLibrary so scanners that touch emuLibrary.Playnite in their ctor can be built.
        private sealed class StubEmuLibrary : IEmuLibrary
        {
            public ILogger Logger => null;
            public IPlayniteAPI Playnite => null;
            public global::EmuLibrary.Settings.Settings Settings => null;
            public IScanCache ScanCache => null;
            public string GetPluginUserDataPath() => null;
            public RomTypeScanner GetScanner(RomType romType) => null;
        }

        // The expected requirement for every RomType. Adding a RomType without adding it here fails
        // EveryRomType_HasAPinnedExpectation below, forcing a deliberate choice.
        public static readonly Dictionary<RomType, bool> Expected = new Dictionary<RomType, bool>
        {
            { RomType.SingleFile, true },
            { RomType.MultiFile, true },
            { RomType.Yuzu, false },
            { RomType.Ps3, false },
        };

        private static RomTypeScanner CreateScanner(RomType rt)
        {
            var attr = typeof(RomType).GetField(rt.ToString())
                .GetCustomAttributes(typeof(RomTypeInfoAttribute), false)
                .Cast<RomTypeInfoAttribute>()
                .Single();
            return (RomTypeScanner)Activator.CreateInstance(attr.ScannerType, new StubEmuLibrary());
        }

        [Theory]
        [InlineData(RomType.SingleFile, true)]
        [InlineData(RomType.MultiFile, true)]
        [InlineData(RomType.Yuzu, false)]
        [InlineData(RomType.Ps3, false)]
        public void RequiresProfileImageExtensions_IsPinned(RomType rt, bool expected)
        {
            Assert.Equal(expected, CreateScanner(rt).RequiresProfileImageExtensions);
        }

        [Fact]
        public void EveryRomType_HasAPinnedExpectation()
        {
            foreach (RomType rt in Enum.GetValues(typeof(RomType)))
            {
                Assert.True(Expected.ContainsKey(rt),
                    $"RomType.{rt} has no pinned RequiresProfileImageExtensions expectation. Decide whether " +
                    $"its scanner needs the emulator profile's image extensions and add it to {nameof(Expected)}.");
                Assert.Equal(Expected[rt], CreateScanner(rt).RequiresProfileImageExtensions);
            }
        }
    }
}
