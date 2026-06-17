using EmuLibrary.PlayniteCommon;
using EmuLibrary.Settings;
using EmuLibrary.Util;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.SingleFile
{
    internal class SingleFileScanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly IPlayniteAPI _playniteAPI;

        public override RomType RomType => RomType.SingleFile;
        public override Guid LegacyPluginId => EmuLibrary.PluginId;

        public SingleFileScanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
            _playniteAPI = emuLibrary.Playnite;
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            var srcPath = mapping.SourcePath;
            var dstPath = mapping.DestinationPathResolved;

            // Each enumeration phase is one governor unit, keyed by the endpoint it scans, so N cheap
            // SingleFile mappings on one host share the per-host budget with the deep scanners. Materialize
            // inside the permit (don't hold it across the lazy yield); the lists are small file listings.
            var installed = RunPhase(dstPath, () => GetInstalledGames(mapping, dstPath, args.CancelToken), args.CancelToken);
            foreach (var game in installed)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;
                yield return game;
            }

            var uninstalled = RunPhase(srcPath, () => GetUninstalledGames(mapping, srcPath, dstPath, args.CancelToken), args.CancelToken);
            foreach (var game in uninstalled)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;
                yield return game;
            }
        }

        // Runs one enumeration phase through the endpoint-aware scan concurrency governor when one is
        // available (acquires a per-host + global permit), falling back to a direct call otherwise.
        private List<GameMetadata> RunPhase(string endpointPath, Func<List<GameMetadata>> work, CancellationToken ct)
        {
            var governor = _emuLibrary.ScanConcurrency;
            if (governor != null)
                return governor.Run(endpointPath, work, ct);
            return work();
        }

        private List<GameMetadata> GetInstalledGames(EmulatorMapping mapping, string dstPath, CancellationToken ct)
        {
            var games = new List<GameMetadata>();
            if (!Directory.Exists(dstPath))
                return games;

            var imageExtensionsLower = mapping.ImageExtensionsLower;
            var fileEnumerator = new SafeFileEnumerator(dstPath, "*.*", SearchOption.TopDirectoryOnly);

            foreach (var file in fileEnumerator)
            {
                if (ct.IsCancellationRequested)
                    return games;

                foreach (var extension in imageExtensionsLower)
                {
                    if (ct.IsCancellationRequested)
                        return games;

                    if (HasMatchingExtension(file, extension) && !DiscFilter.IsExcludedDisc(file.Name))
                    {
                        var baseFileName = StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name));
                        var gameName = StringExtensions.NormalizeGameName(baseFileName);
                        var info = new SingleFileGameInfo()
                        {
                            MappingId = mapping.MappingId,
                            SourcePath = file.Name,
                        };

                        games.Add(new GameMetadata()
                        {
                            Source = EmuLibrary.SourceName,
                            Name = gameName,
                            Roms = new List<GameRom>() { new GameRom(gameName, _playniteAPI.Paths.IsPortable ? file.FullName.Replace(_playniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : file.FullName) },
                            InstallDirectory = _playniteAPI.Paths.IsPortable ? dstPath.Replace(_playniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : dstPath,
                            IsInstalled = true,
                            GameId = info.AsGameId(),
                            Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                            Regions = FileNameUtils.GuessRegionsFromRomName(baseFileName).Select(r => new MetadataNameProperty(r)).ToHashSet<MetadataProperty>(),
                            InstallSize = (ulong)new FileInfo(file.FullName).Length,
                            GameActions = new List<GameAction>() { new GameAction()
                            {
                                Name = $"Play in {mapping.Emulator.Name}",
                                Type = GameActionType.Emulator,
                                EmulatorId = mapping.EmulatorId,
                                EmulatorProfileId = mapping.EmulatorProfileId,
                                IsPlayAction = true,
                            } }
                        });
                    }
                }
            }

            return games;
        }

        private List<GameMetadata> GetUninstalledGames(EmulatorMapping mapping, string srcPath, string dstPath, CancellationToken ct)
        {
            var games = new List<GameMetadata>();
            if (!Directory.Exists(srcPath))
                return games;

            var imageExtensionsLower = mapping.ImageExtensionsLower;
            var fileEnumerator = new SafeFileEnumerator(srcPath, "*.*", SearchOption.TopDirectoryOnly);

            foreach (var file in fileEnumerator)
            {
                if (ct.IsCancellationRequested)
                    return games;

                foreach (var extension in imageExtensionsLower)
                {
                    if (ct.IsCancellationRequested)
                        return games;

                    if (HasMatchingExtension(file, extension) && !DiscFilter.IsExcludedDisc(file.Name))
                    {
                        var equivalentInstalledPath = Path.Combine(dstPath, file.Name);
                        if (File.Exists(equivalentInstalledPath))
                        {
                            continue;
                        }

                        var info = new SingleFileGameInfo()
                        {
                            MappingId = mapping.MappingId,
                            SourcePath = file.Name,
                        };

                        var baseFileName = StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name));
                        var gameName = StringExtensions.NormalizeGameName(baseFileName);

                        games.Add(new GameMetadata()
                        {
                            Source = EmuLibrary.SourceName,
                            Name = gameName,
                            IsInstalled = false,
                            GameId = info.AsGameId(),
                            Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                            Regions = FileNameUtils.GuessRegionsFromRomName(baseFileName).Select(r => new MetadataNameProperty(r)).ToHashSet<MetadataProperty>(),
                            InstallSize = (ulong)new FileInfo(file.FullName).Length,
                            GameActions = new List<GameAction>() { new GameAction()
                            {
                                Name = $"Play in {mapping.Emulator.Name}",
                                Type = GameActionType.Emulator,
                                EmulatorId = mapping.EmulatorId,
                                EmulatorProfileId = mapping.EmulatorProfileId,
                                IsPlayAction = true,
                            } }
                        });
                    }
                }
            }

            return games;
        }

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            // OLD /////////////////////////////////////////////////
            // GameId format - segments divided by '|'.
            // 0 - Was flag string, with only flag ever being * for multi-file. Now is base game path if multifile
            // 1 - Full Rom file source path
            // If no segments present (no '|'), then entire value is Full Rom file source path (1)

            if (!game.GameId.Contains("."))
            {
                gameInfo = null;
                return false;
            }

            var playAction = game.GameActions.Where(ga => ga.IsPlayAction).First();
            if (mapping.RomType != RomType.SingleFile)
            {
                gameInfo = null;
                return false;
            }

            if (game.GameId.Contains("|"))
            {
                // TODO: finish this up for non-PB cases, using existing ELPathInfo code as a base
                var parts = game.GameId.Split('|');

                Debug.Assert(parts.Length == 2, $"GameId is not in expected format (expected 2 parts, got {parts.Length})");

                if (string.IsNullOrEmpty(parts[0]))
                {
                    gameInfo = new RomTypes.SingleFile.SingleFileGameInfo()
                    {
                        MappingId = mapping.MappingId,
                        SourcePath = parts[1].Replace(mapping.SourcePath, "").TrimStart('\\'),
                    };
                    return true;
                }
                else
                {
                    gameInfo = null;
                    return false;
                }
            }
            else
            {
                gameInfo = new RomTypes.SingleFile.SingleFileGameInfo()
                {
                    MappingId = mapping.MappingId,
                    SourcePath = game.GameId,
                };
                return true;
            }
        }

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct)
        {
            return _playniteAPI.Database.Games.TakeWhile(g => !ct.IsCancellationRequested)
                .Where(g =>
            {
                if (g.PluginId != EmuLibrary.PluginId || g.IsInstalled)
                    return false;

                var info = g.GetELGameInfo();
                if (info.RomType != RomType.SingleFile)
                    return false;

                return !File.Exists((info as SingleFileGameInfo).SourceFullPath);
            });
        }
    }
}
