using EmuLibrary.Settings;
using EmuLibrary.Util.Metadata;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.Yuzu
{
    internal class YuzuScanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;

        public override RomType RomType => RomType.Yuzu;
        public override Guid LegacyPluginId => Guid.Parse("545C782C-5478-4B8B-8986-88911D96C420");

        // Yuzu scans by its own hardcoded format list (.xci/.xcz/.nsp/.nsz, see ValidGameExtensions),
        // never the emulator profile's image extensions, so don't require any to be configured.
        public override bool RequiresProfileImageExtensions => false;

        // Yuzu installs into the emulator's NAND and ignores DestinationPath, so it isn't required.
        public override bool RequiresDestinationPath => false;

        // While some games are sold under different title ids in different regions and/or with different language support, this is mostly
        // due to publishing agreements and does not match any technical implementation. All games/console units are all region-free.
        private readonly HashSet<MetadataProperty> _switchRegions = new HashSet<MetadataProperty>() { new MetadataNameProperty("World") };

        public YuzuScanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            if (!Directory.Exists(mapping.EmulatorBasePathResolved))
                yield break;

            var yuzu = new Yuzu(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache, _emuLibrary.ScanConcurrency);

            // titledb names/metadata shadow what's read from the game files (loaded once, refreshed daily, and
            // fail-soft: if it's unavailable the file-derived values are used unchanged).
            var titleDb = new TitleDb(_emuLibrary);

            var installedTitleIds = new HashSet<ulong>();

            #region Import "installed" games
            // Installed state is derived from the emulator's NAND on every scan (like PS3 derives it from disk),
            // so content imported/removed outside Playnite is reflected.
            foreach (var g in yuzu.GetInstalledGames(args.CancelToken))
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                var gameInfo = new YuzuGameInfo()
                {
                    MappingId = mapping.MappingId,
                    TitleId = g.TitleId,
                };

                var newGame = new GameMetadata()
                {
                    Source = EmuLibrary.SourceName,
                    Name = g.Name,
                    Roms = new List<GameRom>() { new GameRom(g.Name, Path.Combine(new string[] { yuzu.NandPath, "user", "Contents", "registered", g.ProgramNcaSubPath })) },
                    InstallDirectory = mapping.EmulatorBasePath,
                    IsInstalled = true,
                    GameId = gameInfo.AsGameId(),
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    Regions = _switchRegions,
                    Version = g.Version,
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

                if (titleDb.TryGet(g.TitleId, out var installedMeta))
                    installedMeta.ApplyTo(newGame);

                yield return newGame;
            }
            #endregion

            #region Import "uninstalled" games
            if (!Directory.Exists(mapping.SourcePath))
                yield break;

            foreach (var g in yuzu.GetUninstalledGamesFromDir(mapping.SourcePath, args.CancelToken))
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                if (installedTitleIds.Contains(g.TitleId))
                    continue;

                var gameInfo = new YuzuGameInfo()
                {
                    MappingId = mapping.MappingId,
                    TitleId = g.TitleId,
                };

                var newGame = new GameMetadata()
                {
                    Source = EmuLibrary.SourceName,
                    Name = g.Name,
                    IsInstalled = false,
                    GameId = gameInfo.AsGameId(),
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    Regions = _switchRegions,
                    InstallSize = g.InstallSize,
                    GameActions = new List<GameAction>() {
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

                if (titleDb.TryGet(g.TitleId, out var uninstalledMeta))
                    uninstalledMeta.ApplyTo(newGame);

                yield return newGame;
            }
            #endregion
        }

        // Resolves a single title's composite (base + latest update + DLC) from the source dir on demand,
        // IScanCache-backed via Yuzu.GetUninstalledGamesFromDir. The Yuzu analog of Ps3Scanner.BuildTitle;
        // shared by the install controller (which re-derives at install time). Returns null if not found.
        internal YuzuTitle BuildTitle(EmulatorMapping mapping, ulong titleId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(mapping.SourcePath) || !Directory.Exists(mapping.SourcePath))
                return null;

            var yuzu = new Yuzu(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache, _emuLibrary.ScanConcurrency);
            return yuzu.GetUninstalledGamesFromDir(mapping.SourcePath, ct).FirstOrDefault(t => t.TitleId == titleId);
        }

        // TODO: This isn't as straightforward as some of the other types. We would need to scan the source folder for all base Title IDs
        // and then look for any games of RomType.Yuzu that are uninstalled and aren't in the list of base Title IDs
        //
        // If it is decided to never implement this, then consider making base method virtual and returning empty, rather than forcing
        // derived classes to implement it just to do the same thing
        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct) =>
            Enumerable.Empty<Game>();

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            gameInfo = null;
            return false;
        }
    }
}
