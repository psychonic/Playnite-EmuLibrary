using EmuLibrary;
using EmuLibrary.RomTypes;
using EmuLibrary.RomTypes.Vita;
using EmuLibrary.Settings;
using EmuLibrary.Tests.Util;
using EmuLibrary.Util.ScanCache;
using EmuLibrary.Util.ScanConcurrency;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Vita
{
    // Covers the Vita scanner's title-id association via BuildTitle (Shape 1 ux0-style and Shape 2 per-title
    // folders, orphan patch, base-only), the Family-B (all-in-order) update ordering the install controller
    // drives through CompositeContent, and the ux0 resolver over a synthetic Vita3K config.yml.
    public class VitaScannerTests
    {
        private const string TitleId = "PCSE00890";

        // Minimal IEmuLibrary — BuildTitle only touches ScanCache (null-safe) and, on a read failure, Logger.
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

        private static VitaScanner NewScanner() => new VitaScanner(new StubEmuLibrary());

        // Creates an "<parent>/<kind>/<titleId>" tree with a sce_sys/param.sfo carrying TITLE_ID (+ optional
        // APP_VER), and an eboot.bin so the tree has real content.
        private static string MakeTree(string parent, string kind, string titleId, string appVer = null)
        {
            var tree = Path.Combine(parent, kind, titleId);
            var sceSys = Path.Combine(tree, "sce_sys");
            Directory.CreateDirectory(sceSys);
            File.WriteAllBytes(Path.Combine(tree, "eboot.bin"), new byte[64]);

            var extra = appVer != null ? new[] { ("APP_VER", appVer) } : Array.Empty<(string, string)>();
            File.WriteAllBytes(Path.Combine(sceSys, "param.sfo"),
                PspTestImage.BuildSfo(titleId, titleIdOnly: true, extra: extra));
            return tree;
        }

        [Fact]
        public void BuildTitle_Shape1_PairsPatchByTitleId()
        {
            var src = NewTempDir();
            try
            {
                // ux0-style: app/ and patch/ directly under the source (the container is the source root).
                MakeTree(src, "app", TitleId, "01.00");
                MakeTree(src, "patch", TitleId, "01.02");

                var mapping = new EmulatorMapping { SourcePath = src };
                var title = NewScanner().BuildTitle(mapping, src, TitleId, CancellationToken.None);

                Assert.NotNull(title);
                Assert.Equal(TitleId, title.TitleId);
                Assert.Equal("", title.SourceFolder); // container == source root
                Assert.Single(title.Updates);
                Assert.Equal(VitaContentKind.Patch, title.Updates[0].Kind);
                Assert.Equal("01.02", title.Updates[0].AppVer);
                Assert.Equal("01.02", title.Version); // latest patch APP_VER
            }
            finally { Directory.Delete(src, true); }
        }

        [Fact]
        public void BuildTitle_Shape2_PerTitleFolder_Pairs()
        {
            var src = NewTempDir();
            try
            {
                // Per-title folder: <src>/<folder>/app/<id> and .../patch/<id>.
                var container = Path.Combine(src, "PCSE00890 - 10 Second Ninja X");
                MakeTree(container, "app", TitleId, "01.00");
                MakeTree(container, "patch", TitleId, "01.05");

                var mapping = new EmulatorMapping { SourcePath = src };
                var title = NewScanner().BuildTitle(mapping, container, TitleId, CancellationToken.None);

                Assert.NotNull(title);
                Assert.Equal(TitleId, title.TitleId);
                Assert.Equal("PCSE00890 - 10 Second Ninja X", title.SourceFolder);
                Assert.Single(title.Updates);
                Assert.Equal("01.05", title.Version);
            }
            finally { Directory.Delete(src, true); }
        }

        [Fact]
        public void BuildTitle_BaseOnly_HasNoUpdates()
        {
            var src = NewTempDir();
            try
            {
                MakeTree(src, "app", TitleId, "01.00");
                // No patch/<id> tree → base-only.

                var mapping = new EmulatorMapping { SourcePath = src };
                var title = NewScanner().BuildTitle(mapping, src, TitleId, CancellationToken.None);

                Assert.NotNull(title);
                Assert.Equal(TitleId, title.TitleId);
                Assert.Empty(title.Updates);
                Assert.Equal("01.00", title.Version); // base APP_VER
            }
            finally { Directory.Delete(src, true); }
        }

        [Fact]
        public void BuildTitle_OrphanPatch_IsUnmatched()
        {
            var src = NewTempDir();
            try
            {
                // A patch tree with no matching base app tree yields no title (orphan patch is ignored).
                MakeTree(src, "patch", TitleId, "01.02");

                var mapping = new EmulatorMapping { SourcePath = src };
                Assert.Null(NewScanner().BuildTitle(mapping, src, TitleId, CancellationToken.None));
            }
            finally { Directory.Delete(src, true); }
        }

        // Family B installs all updates in ascending version order — versions must sort numerically (01.10 >
        // 01.02), never lexically. Exactly what CompositeContent + the controller's VersionKey drive.
        [Fact]
        public void UpdateOrdering_IsNumericAscending()
        {
            var refs = new List<VitaContentRef>
            {
                new VitaContentRef { AppVer = "02.00", AppVerParsed = new Version(2, 0) },
                new VitaContentRef { AppVer = "01.10", AppVerParsed = new Version(1, 10) },
                new VitaContentRef { AppVer = "01.02", AppVerParsed = new Version(1, 2) },
                new VitaContentRef { AppVer = "01.00", AppVerParsed = new Version(1, 0) },
            };

            var ordered = CompositeContent.SelectUpdatesToInstall(
                refs, UpdateInstallStrategy.InstallAllUpdatesInOrder, x => x.AppVerParsed);

            Assert.Equal(new[] { "01.00", "01.02", "01.10", "02.00" }, ordered.Select(r => r.AppVer).ToArray());
        }

        [Fact]
        public void GetUx0_FromConfigPrefPath()
        {
            var dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "config.yml"),
                    "pref-path: \"D:\\\\Vita3K\\\\data\"\nsome-other-key: 42\nflag: true\n");

                Assert.Equal(Path.Combine(@"D:\Vita3K\data", "ux0"), VitaEmulator.GetUx0(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ParsePrefPath_ReadsValue()
        {
            Assert.Equal(@"D:\Vita3K\data",
                VitaEmulator.ParsePrefPathFromConfigYml("pref-path: \"D:\\\\Vita3K\\\\data\"\nflag: true\n"));
        }

        [Fact]
        public void GetUx0_MissingPrefPath_FallsBackToBaseUx0()
        {
            var dir = NewTempDir();
            try
            {
                // No config.yml at all → fallback to <base>/ux0.
                Assert.Equal(Path.Combine(dir, "ux0"), VitaEmulator.GetUx0(dir));

                // Empty pref-path value → same fallback.
                File.WriteAllText(Path.Combine(dir, "config.yml"), "pref-path: \"\"\nflag: true\n");
                Assert.Equal(Path.Combine(dir, "ux0"), VitaEmulator.GetUx0(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ParsePrefPath_MissingKey_ReturnsNull()
        {
            Assert.Null(VitaEmulator.ParsePrefPathFromConfigYml("flag: true\nnum: 7\n"));
            Assert.Null(VitaEmulator.ParsePrefPathFromConfigYml(""));
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eltest-vita-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
