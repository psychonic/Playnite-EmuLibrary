using EmuLibrary.RomTypes.Xbox360;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Xbox360
{
    using UnitInfo = Xbox360Scanner.Xbox360UnitInfo;

    public class Xbox360GroupingTests
    {
        private const uint TitleA = 0x5841085E;
        private const uint TitleB = 0x45410869;
        private const uint MediaM = 0x0039F097;
        private const uint MediaX = 0x01272092;

        private static UnitInfo Disc(uint title, uint media, string path) => new UnitInfo
        {
            Path = path,
            Kind = Xbox360ContentKind.DiscImage,
            TitleId = title,
            MediaId = media,
            Version = 1,
            ContentSize = 1000,
        };

        private static UnitInfo Tu(uint title, uint media, uint version, string path) => new UnitInfo
        {
            Path = path,
            Kind = Xbox360ContentKind.TitleUpdate,
            TitleId = title,
            MediaId = media,
            Version = version,
            ContentType = 0x000B0000,
            ContentSize = 10,
        };

        private static UnitInfo Dlc(uint title, uint media, string path) => new UnitInfo
        {
            Path = path,
            Kind = Xbox360ContentKind.Dlc,
            TitleId = title,
            MediaId = media,
            Version = 0,
            ContentType = 0x00000002,
            ContentSize = 5,
        };

        private static UnitInfo Zar(uint title, uint media, string path) => new UnitInfo
        {
            Path = path,
            Kind = Xbox360ContentKind.ZarImage,
            TitleId = title,
            MediaId = media,
            Version = 1,
            ContentSize = 800,
        };

        private static UnitInfo Digital(uint title, uint media, uint contentType, string displayName, string path) => new UnitInfo
        {
            Path = path,
            Kind = Xbox360ContentKind.DigitalBase,
            TitleId = title,
            MediaId = media,
            Version = 1,
            ContentType = contentType,
            ContentSize = 2000,
            DisplayName = displayName,
        };

        [Fact]
        public void LatestUpdateOnly_KeepsSingleMax_ByMediaIdMatch()
        {
            var baseUnit = Disc(TitleA, MediaM, @"C:\src\Marathon (USA).iso");
            var updates = new List<UnitInfo>
            {
                Tu(TitleA, MediaM, 2, @"C:\src\tu\TU_v2"),
                Tu(TitleA, MediaM, 5, @"C:\src\tu\TU_v5"),
                Tu(TitleA, MediaX, 9, @"C:\src\tu\TU_wrongmedia"), // different MediaID — must NOT attach
            };

            var title = Xbox360Scanner.ComposeTitle(baseUnit, updates, new List<UnitInfo>());

            var only = Assert.Single(title.Updates);
            Assert.Equal(5U, only.Version);
            Assert.Equal("Marathon", title.Name);
        }

        [Fact]
        public void NonMatchingMediaId_UpdateDoesNotAttach()
        {
            var baseUnit = Disc(TitleA, MediaM, @"C:\src\game.iso");
            var updates = new List<UnitInfo> { Tu(TitleA, MediaX, 9, @"C:\src\tu\TU_x") };

            var title = Xbox360Scanner.ComposeTitle(baseUnit, updates, new List<UnitInfo>());

            Assert.Empty(title.Updates);
        }

        [Fact]
        public void Dlc_AttachesByTitleId_IgnoringMediaId()
        {
            var baseUnit = Disc(TitleA, MediaM, @"C:\src\game.iso");
            var dlc = new List<UnitInfo>
            {
                Dlc(TitleA, 0, @"C:\src\dlc\addon1"),   // MediaID 0 — still attaches by TitleID
                Dlc(TitleB, 0, @"C:\src\dlc\other"),    // different TitleID — must NOT attach
            };

            var title = Xbox360Scanner.ComposeTitle(baseUnit, new List<UnitInfo>(), dlc);

            var only = Assert.Single(title.DlcRefs);
            Assert.Equal("addon1", only.ContentName);
        }

        [Fact]
        public void NoUpdatesOrDlc_BaseOnly()
        {
            var baseUnit = Disc(TitleA, MediaM, @"C:\src\game.iso");

            var title = Xbox360Scanner.ComposeTitle(baseUnit, new List<UnitInfo>(), new List<UnitInfo>());

            Assert.Empty(title.Updates);
            Assert.Empty(title.DlcRefs);
            Assert.Equal(TitleA, title.TitleId);
            Assert.Equal(MediaM, title.MediaId);
            Assert.Equal(1000UL, title.InstallSize);
        }

        [Fact]
        public void InstallSize_SumsBasePlusSelectedUpdatePlusDlc()
        {
            var baseUnit = Disc(TitleA, MediaM, @"C:\src\game.iso"); // 1000
            var updates = new List<UnitInfo>
            {
                Tu(TitleA, MediaM, 2, @"C:\src\tu\TU_v2"), // 10, not selected
                Tu(TitleA, MediaM, 5, @"C:\src\tu\TU_v5"), // 10, selected
            };
            var dlc = new List<UnitInfo> { Dlc(TitleA, 0, @"C:\src\dlc\addon1") }; // 5

            var title = Xbox360Scanner.ComposeTitle(baseUnit, updates, dlc);

            Assert.Equal(1000UL + 10UL + 5UL, title.InstallSize);
        }

        [Fact]
        public void DigitalBase_UsesStfsDisplayNameAndDigitalKind()
        {
            var baseUnit = Digital(TitleA, MediaM, 0x000D0000, "'Splosion Man", @"C:\src\5841085E\000D0000\E424CFB358");
            var tu = Tu(TitleA, MediaM, 3, @"C:\src\tu\TU_v3");
            var dlc = Dlc(TitleA, 0, @"C:\src\dlc\addon");

            var title = Xbox360Scanner.ComposeTitle(baseUnit, new List<UnitInfo> { tu }, new List<UnitInfo> { dlc });

            Assert.Equal(Xbox360BaseKind.DigitalPackage, title.BaseKind);
            Assert.Equal("'Splosion Man", title.Name); // not the hex content-id leaf
            Assert.Single(title.Updates);
            Assert.Single(title.DlcRefs);
        }

        [Fact]
        public void ZarBase_MapsToZarArchiveKind_KeepsFilename_AndGroups()
        {
            var baseUnit = Zar(TitleA, MediaM, @"C:\src\Marathon (USA).zar");
            var updates = new List<UnitInfo> { Tu(TitleA, MediaM, 4, @"C:\src\tu\TU_v4") };
            var dlc = new List<UnitInfo> { Dlc(TitleA, 0, @"C:\src\dlc\addon1") };

            var title = Xbox360Scanner.ComposeTitle(baseUnit, updates, dlc);

            Assert.Equal(Xbox360BaseKind.ZarArchive, title.BaseKind);
            Assert.Equal("Marathon", title.Name); // No-Intro filename, like a disc — not an STFS DisplayName
            Assert.Single(title.Updates);
            Assert.Single(title.DlcRefs);
        }

        [Fact]
        public void SelectScanBases_PrefersDiscWhenKeyCollides()
        {
            var disc = new List<UnitInfo> { Disc(TitleA, MediaM, @"C:\src\game.iso") };
            var digital = new List<UnitInfo>
            {
                Digital(TitleA, MediaM, 0x000D0000, "Game (Digital)", @"C:\src\dig\base"), // collides -> shadowed
                Digital(TitleB, MediaX, 0x000D0000, "Other", @"C:\src\dig\other"),         // unique -> kept
            };

            var bases = Xbox360Scanner.SelectScanBases(disc, digital);

            Assert.Equal(2, bases.Count);
            Assert.Single(bases, b => b.TitleId == TitleA && b.Kind == Xbox360ContentKind.DiscImage);
            Assert.Single(bases, b => b.TitleId == TitleB && b.Kind == Xbox360ContentKind.DigitalBase);
        }

        [Fact]
        public void ContentTreeDestPath_IsBuiltUnderNonSaveXuid()
        {
            var dir = XeniaEmulator.GetContentTypeDir(@"C:\Xenia\content", TitleA, 0x000B0000);
            var expected = Path.Combine(@"C:\Xenia\content", "0000000000000000", "5841085E", "000B0000");
            Assert.Equal(expected, dir);
        }
    }
}
