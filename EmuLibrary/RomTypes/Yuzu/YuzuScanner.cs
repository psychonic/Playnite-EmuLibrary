using EmuLibrary.Settings;
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

        private readonly Dictionary<Guid, SourceDirCache> _mappingCaches;

        // While some games are sold under different title ids in different regions and/or with different language support, this is mostly
        // due to publishing agreements and does not match any technical implementation. All games/console units are all region-free.
        private readonly HashSet<MetadataProperty> _switchRegions = new HashSet<MetadataProperty>() { new MetadataNameProperty("World") };

        public YuzuScanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
            _mappingCaches = new Dictionary<Guid, SourceDirCache>();
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            if (!_mappingCaches.TryGetValue(mapping.MappingId, out var mappingCache))
            {
                mappingCache = new SourceDirCache(_emuLibrary, mapping);
                _mappingCaches.Add(mapping.MappingId, mappingCache);
            }

            if (mappingCache.IsDirty)
            {
                mappingCache.Refresh(args.CancelToken);
            }

            var installedGames = new HashSet<ulong>();

            if (!Directory.Exists(mapping.EmulatorBasePathResolved))
                yield break;

            var yuzu = new Yuzu(mapping.EmulatorBasePathResolved, _emuLibrary.Logger);

            #region Import "installed" games
            foreach (var g in mappingCache.TheCache.InstalledGames.Values)
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
                    Name = g.Title,
                    Roms = new List<GameRom>() { new GameRom(g.Title, Path.Combine(new string[] { yuzu.NandPath, "user", "Contents", "registered", g.ProgramNcaSubPath })) },
                    InstallDirectory = mapping.EmulatorBasePath,
                    IsInstalled = true,
                    GameId = gameInfo.AsGameId(),
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    Regions = _switchRegions,
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

                installedGames.Add(g.TitleId);

                yield return newGame;
            }
            #endregion

            #region Import "uninstalled" games
            if (!Directory.Exists(mapping.SourcePath))
                yield break;

            foreach (var g in mappingCache.TheCache.UninstalledGames.Values)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                if (installedGames.Contains(g.TitleId))
                    continue;

                var gameInfo = new YuzuGameInfo()
                {
                    MappingId = mapping.MappingId,
                    TitleId = g.TitleId,
                };

                var newGame = new GameMetadata()
                {
                    Source = EmuLibrary.SourceName,
                    Name = g.Title,
                    IsInstalled = false,
                    GameId = gameInfo.AsGameId(),
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    Regions = _switchRegions,
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

                yield return newGame;
            }

            #endregion
        }

        // TODO: This isn't as straightforward as some of the other types. We would need to scan the source folder for all base Title IDs
        // and then look for any games of RomType.Yuzu that are uninstalled and aren't in the list of base Title IDs
        //
        // If it is decided to never implement this, then consider making base method virtual and returning empty, rather than forcing
        // derived classes to implement it just to do the same thing
        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct) =>
            Enumerable.Empty<Game>();

        public SourceDirCache GetCacheForMapping(Guid mappingId) => _mappingCaches[mappingId];

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            gameInfo = null;
            return false;
        }
    }
}
