using EmuLibrary;
using EmuLibrary.RomTypes;
using EmuLibrary.RomTypes.Psp;
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

namespace EmuLibrary.Tests.RomTypes.Psp
{
    // Covers the PSP scanner's association (image DISC_ID ↔ sibling title-id folder) via BuildTitle, and the
    // Family-B (all-in-order) update ordering the install controller drives through CompositeContent.
    public class PspScannerTests
    {
        private const string DiscId = "ULUS10538";

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

        private static PspScanner NewScanner() => new PspScanner(new StubEmuLibrary());

        [Fact]
        public void BuildTitle_PairsSiblingFolderByDiscId()
        {
            var src = NewTempDir();
            try
            {
                var imagePath = Path.Combine(src, "Armored Core - Last Raven Portable (USA).iso");
                File.WriteAllBytes(imagePath, PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)));

                // Sibling update/DLC payload folder named for the disc id.
                var payload = Path.Combine(src, DiscId);
                Directory.CreateDirectory(payload);
                File.WriteAllBytes(Path.Combine(payload, "PBOOT.PBP"), new byte[16]);

                var mapping = new EmulatorMapping { SourcePath = src };
                var title = NewScanner().BuildTitle(mapping, imagePath, CancellationToken.None);

                Assert.NotNull(title);
                Assert.Equal(DiscId, title.TitleId);
                Assert.Single(title.Updates);
                var item = title.Updates[0];
                Assert.True(item.IsDirectory);
                Assert.Equal(PspRoute.Game, item.Route);
                Assert.Equal(DiscId, item.DestSubPath);
                Assert.Equal(payload, item.SourcePath);
            }
            finally { Directory.Delete(src, true); }
        }

        [Fact]
        public void BuildTitle_PlainImage_IsBaseOnly()
        {
            var src = NewTempDir();
            try
            {
                var imagePath = Path.Combine(src, "Grand Theft Auto - VCS (USA).iso");
                File.WriteAllBytes(imagePath, PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)));
                // No sibling folder named ULUS10538 → base-only.

                var mapping = new EmulatorMapping { SourcePath = src };
                var title = NewScanner().BuildTitle(mapping, imagePath, CancellationToken.None);

                Assert.NotNull(title);
                Assert.Equal(DiscId, title.TitleId);
                Assert.Empty(title.Updates);
                Assert.Empty(title.Dlcs);
                Assert.False(title.BaseRef.IsDirectory);
            }
            finally { Directory.Delete(src, true); }
        }

        [Fact]
        public void BuildTitle_MissingImage_ReturnsNull()
        {
            var mapping = new EmulatorMapping { SourcePath = Path.GetTempPath() };
            Assert.Null(NewScanner().BuildTitle(mapping, Path.Combine(Path.GetTempPath(), "nope.iso"), CancellationToken.None));
        }

        // Family B installs all updates in ascending version order — versions must sort numerically (01.10 >
        // 01.02), never lexically. This is exactly what CompositeContent + the controller's VersionKey drive.
        [Fact]
        public void UpdateOrdering_IsNumericAscending()
        {
            var refs = new List<PspContentRef>
            {
                new PspContentRef { DestSubPath = "b", Version = new Version(2, 0) },
                new PspContentRef { DestSubPath = "a", Version = new Version(1, 10) },
                new PspContentRef { DestSubPath = "c", Version = new Version(1, 2) },
                new PspContentRef { DestSubPath = "d", Version = new Version(1, 0) },
            };

            var ordered = CompositeContent.SelectUpdatesToInstall(
                refs, UpdateInstallStrategy.InstallAllUpdatesInOrder, x => x.Version);

            Assert.Equal(new[] { "d", "c", "a", "b" }, ordered.Select(r => r.DestSubPath).ToArray());
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eltest-psp-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
