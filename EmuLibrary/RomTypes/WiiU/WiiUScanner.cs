using EmuLibrary.Settings;
using EmuLibrary.Util.Metadata;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.WiiU
{
    internal class WiiUScanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;

        public override RomType RomType => RomType.WiiU;

        // The old standalone CemuLibrary extension's plugin id (for future legacy migration).
        public override Guid LegacyPluginId => Guid.Parse("BF9D9CD7-3761-424F-946F-F5D271A6B2D8");

        // Wii U scans by its own format logic (NUS/loadiine/...), not the emulator profile's image extensions.
        public override bool RequiresProfileImageExtensions => false;

        // Installs land in DestinationPath (a copied/converted .wua, .wux or loadiine folder).
        public override bool RequiresDestinationPath => true;

        public WiiUScanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            var cemu = new Cemu(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);

            // GameTDB (wiiutdb) is keyed by product code, so it's optional enrichment layered over the
            // file-derived name; fail-soft if unavailable.
            var gameTdb = new GameTdb(_emuLibrary, "wiiutdb");

            var installedTitleIds = new HashSet<ulong>();

            #region Import "installed" games (derived from the destination folder on disk)
            foreach (var g in cemu.GetInstalledTitles(mapping.DestinationPathResolved, args.CancelToken))
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                var gameInfo = new WiiUGameInfo { MappingId = mapping.MappingId, TitleId = g.TitleId };

                var newGame = new GameMetadata()
                {
                    Source = EmuLibrary.SourceName,
                    Name = g.Name,
                    Roms = new List<GameRom>() { new GameRom(g.Name, g.LaunchPath) },
                    InstallDirectory = mapping.DestinationPath,
                    IsInstalled = true,
                    GameId = gameInfo.AsGameId(),
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    InstallSize = g.InstallSize,
                    GameActions = new List<GameAction>()
                    {
                        new GameAction()
                        {
                            Name = $"Play in {mapping.Emulator.Name}",
                            Type = GameActionType.Emulator,
                            EmulatorId = mapping.EmulatorId,
                            EmulatorProfileId = mapping.EmulatorProfileId,
                            IsPlayAction = true,
                        }
                    }
                };

                installedTitleIds.Add(g.TitleId);
                yield return newGame;
            }
            #endregion

            #region Import "uninstalled" games (from the source folder)
            if (!Directory.Exists(mapping.SourcePath))
                yield break;

            foreach (var t in cemu.GetTitlesFromDir(mapping.SourcePath, args.CancelToken))
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                if (installedTitleIds.Contains(t.TitleId))
                    continue;

                var gameInfo = new WiiUGameInfo { MappingId = mapping.MappingId, TitleId = t.TitleId };

                var newGame = new GameMetadata()
                {
                    Source = EmuLibrary.SourceName,
                    Name = t.Name,
                    IsInstalled = false,
                    GameId = gameInfo.AsGameId(),
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    InstallSize = t.InstallSize,
                    GameActions = new List<GameAction>()
                    {
                        new GameAction()
                        {
                            Name = $"Play in {mapping.Emulator.Name}",
                            Type = GameActionType.Emulator,
                            EmulatorId = mapping.EmulatorId,
                            EmulatorProfileId = mapping.EmulatorProfileId,
                            IsPlayAction = true,
                        }
                    }
                };

                if (!string.IsNullOrEmpty(t.GameTdbId) && gameTdb.TryGet(t.GameTdbId, out var meta))
                    meta.ApplyTo(newGame);

                yield return newGame;
            }
            #endregion
        }

        // Re-derives a single title's composite from the source on demand (used by the install controller).
        internal WiiUTitle BuildTitle(EmulatorMapping mapping, ulong baseTitleId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(mapping.SourcePath) || !Directory.Exists(mapping.SourcePath))
                return null;

            var cemu = new Cemu(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);
            return cemu.BuildTitle(mapping.SourcePath, baseTitleId, ct);
        }

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct) =>
            Enumerable.Empty<Game>();

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            gameInfo = null;
            return false;
        }
    }
}
