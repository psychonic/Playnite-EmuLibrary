using EmuLibrary.RomTypes;
using EmuLibrary.RomTypes.SingleFile;
using EmuLibrary.Settings;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Xunit;

namespace EmuLibrary.Tests.Settings
{
    // Covers MappingEditReconciler's database-facing halves: identifying the games that belong to a mapping
    // (the GameId->MappingId decode) and re-pointing them at new emulator/platform values. The VerifySettings
    // orchestration (RomType block, migration-offer dialog) is UI/settings-lifecycle bound and left to a
    // Playnite load; these tests pin the reconciliation logic itself.
    public class MappingEditReconcilerTests
    {
        private static readonly Guid MappingA = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid MappingB = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        public MappingEditReconcilerTests()
        {
            ELGameInfo.RegisterProtoSubTypes();
        }

        private static Game EmuLibraryGame(Guid mappingId, string sourcePath, Guid emulatorId, string profileId, Guid platformId)
        {
            var info = new SingleFileGameInfo { MappingId = mappingId, SourcePath = sourcePath };
            return new Game
            {
                Name = sourcePath,
                PluginId = global::EmuLibrary.EmuLibrary.PluginId,
                GameId = info.AsGameId(),
                GameActions = new ObservableCollection<GameAction>
                {
                    new GameAction { Type = GameActionType.Emulator, IsPlayAction = true, EmulatorId = emulatorId, EmulatorProfileId = profileId },
                },
                PlatformIds = new List<Guid> { platformId },
            };
        }

        [Fact]
        public void GamesForMapping_ReturnsOnlyMatchingMappingInCurrentFormat()
        {
            var oldEmu = Guid.NewGuid();
            var oldPlat = Guid.NewGuid();

            var a1 = EmuLibraryGame(MappingA, "a1.zip", oldEmu, "p", oldPlat);
            var a2 = EmuLibraryGame(MappingA, "a2.zip", oldEmu, "p", oldPlat);
            var b1 = EmuLibraryGame(MappingB, "b1.zip", oldEmu, "p", oldPlat);
            var foreign = new Game { Name = "foreign", PluginId = Guid.NewGuid(), GameId = "!0whatever" };
            var legacy = new Game { Name = "legacy", PluginId = global::EmuLibrary.EmuLibrary.PluginId, GameId = @"C:\roms\legacy.zip" };
            var undecodable = new Game { Name = "bad", PluginId = global::EmuLibrary.EmuLibrary.PluginId, GameId = "!0not-valid-base64!!" };

            var api = new FakePlayniteAPI(new[] { a1, a2, b1, foreign, legacy, undecodable });

            var result = MappingEditReconciler.GamesForMapping(api, MappingA).ToList();

            Assert.Equal(new[] { a1, a2 }, result);
        }

        [Fact]
        public void RepointGames_UpdatesActionsAndPlatform_OnlyForTargetMapping()
        {
            var oldEmu = Guid.NewGuid();
            var oldPlat = Guid.NewGuid();
            var newEmu = Guid.NewGuid();
            var newPlat = Guid.NewGuid();

            var a1 = EmuLibraryGame(MappingA, "a1.zip", oldEmu, "old", oldPlat);
            var a2 = EmuLibraryGame(MappingA, "a2.zip", oldEmu, "old", oldPlat);
            var b1 = EmuLibraryGame(MappingB, "b1.zip", oldEmu, "old", oldPlat);

            var api = new FakePlayniteAPI(new[] { a1, a2, b1 });

            var count = MappingEditReconciler.RepointGames(api, MappingA, newEmu, "new", newPlat, null);

            Assert.Equal(2, count);
            foreach (var g in new[] { a1, a2 })
            {
                var action = g.GameActions.Single();
                Assert.Equal(newEmu, action.EmulatorId);
                Assert.Equal("new", action.EmulatorProfileId);
                Assert.Equal(new[] { newPlat }, g.PlatformIds);
            }

            // Mapping B's game is untouched.
            var bAction = b1.GameActions.Single();
            Assert.Equal(oldEmu, bAction.EmulatorId);
            Assert.Equal("old", bAction.EmulatorProfileId);
            Assert.Equal(new[] { oldPlat }, b1.PlatformIds);
        }

        [Fact]
        public void RepointGames_WithNullPlatform_LeavesPlatformIdsUnchanged()
        {
            var oldPlat = Guid.NewGuid();
            var newEmu = Guid.NewGuid();
            var a1 = EmuLibraryGame(MappingA, "a1.zip", Guid.NewGuid(), "old", oldPlat);

            var api = new FakePlayniteAPI(new[] { a1 });

            var count = MappingEditReconciler.RepointGames(api, MappingA, newEmu, "new", null, null);

            Assert.Equal(1, count);
            Assert.Equal(newEmu, a1.GameActions.Single().EmulatorId);
            Assert.Equal(new[] { oldPlat }, a1.PlatformIds); // platform left alone
        }

        [Fact]
        public void RepointGames_ToleratesNullGameActions()
        {
            var info = new SingleFileGameInfo { MappingId = MappingA, SourcePath = "a1.zip" };
            var game = new Game
            {
                Name = "a1",
                PluginId = global::EmuLibrary.EmuLibrary.PluginId,
                GameId = info.AsGameId(),
                GameActions = null,
                PlatformIds = null,
            };
            var newPlat = Guid.NewGuid();
            var api = new FakePlayniteAPI(new[] { game });

            var count = MappingEditReconciler.RepointGames(api, MappingA, Guid.NewGuid(), "new", newPlat, null);

            Assert.Equal(1, count);
            Assert.Equal(new[] { newPlat }, game.PlatformIds);
        }

        [Fact]
        public void RepointGames_ReturnsZero_WhenNoGamesMatch()
        {
            var api = new FakePlayniteAPI(Array.Empty<Game>());
            var count = MappingEditReconciler.RepointGames(api, MappingA, Guid.NewGuid(), "new", Guid.NewGuid(), null);
            Assert.Equal(0, count);
        }
    }
}
