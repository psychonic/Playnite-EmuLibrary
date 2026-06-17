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

namespace EmuLibrary.RomTypes.MultiFile
{
    internal class MultiFileScanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly IPlayniteAPI _playniteAPI;

        public override RomType RomType => RomType.MultiFile;
        public override Guid LegacyPluginId => EmuLibrary.PluginId;

        public MultiFileScanner(IEmuLibrary emuLibrary) : base(emuLibrary)
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
            // MultiFile mappings on one host share the per-host budget with the deep scanners. Materialize
            // inside the permit (don't hold it across the lazy yield); the lists are small folder listings.
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

                if (file.Attributes.HasFlag(FileAttributes.Directory) && !DiscFilter.IsExcludedDisc(file.Name))
                {
                    var dirEnumerator = new SafeFileEnumerator(file.FullName, "*.*", SearchOption.AllDirectories);
                    // First matching rom of first valid extension that has any matches. Ex. for "m3u,cue,bin", make sure we don't grab a bin file when there's an m3u or cue handy
                    var rom = LoadFileSelector.Select(dirEnumerator, f => f.Extension.TrimStart('.').ToLower(), imageExtensionsLower);
                    if (rom != null)
                    {
                        var baseFileName = StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name));
                        var gameName = StringExtensions.NormalizeGameName(baseFileName);
                        var info = new MultiFileGameInfo()
                        {
                            MappingId = mapping.MappingId,

                            // Relative to mapping.SourcePath
                            SourceFilePath = Path.Combine(file.Name, rom.FullName.Replace(file.FullName, "").TrimStart('\\')),
                            SourceBaseDir = Path.Combine(file.Name),
                        };

                        games.Add(new GameMetadata()
                        {
                            Source = EmuLibrary.SourceName,
                            Name = gameName,
                            Roms = new List<GameRom>() { new GameRom(gameName, _playniteAPI.Paths.IsPortable ? rom.FullName.Replace(_playniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : rom.FullName) },
                            InstallDirectory = _playniteAPI.Paths.IsPortable ? file.FullName.Replace(_playniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : file.FullName,
                            IsInstalled = true,
                            GameId = info.AsGameId(),
                            Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                            Regions = FileNameUtils.GuessRegionsFromRomName(baseFileName).Select(r => new MetadataNameProperty(r)).ToHashSet<MetadataProperty>(),
                            InstallSize = (ulong)dirEnumerator.Where(f => !f.Attributes.HasFlag(FileAttributes.Directory)).Select(f => new FileInfo(f.FullName)).Sum(f => f.Length),
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

                if (file.Attributes.HasFlag(FileAttributes.Directory) && !DiscFilter.IsExcludedDisc(file.Name))
                {
                    var dirEnumerator = new SafeFileEnumerator(file.FullName, "*.*", SearchOption.AllDirectories);
                    // First matching rom of first valid extension that has any matches. Ex. for "m3u,cue,bin", make sure we don't grab a bin file when there's an m3u or cue handy
                    var rom = LoadFileSelector.Select(dirEnumerator, f => f.Extension.TrimStart('.').ToLower(), imageExtensionsLower);
                    if (rom != null)
                    {
                        var fileInfo = new FileInfo(rom.FullName);
                        var dirInfo = new DirectoryInfo(file.FullName);
                        var equivalentInstalledPath = Path.Combine(dstPath, Path.Combine(new string[] { dirInfo.Name, fileInfo.Directory.FullName.Replace(dirInfo.FullName, "").TrimStart('\\'), fileInfo.Name }));

                        if (File.Exists(equivalentInstalledPath))
                        {
                            continue;
                        }

                        var info = new MultiFileGameInfo()
                        {
                            MappingId = mapping.MappingId,

                            // Relative to mapping.SourcePath
                            SourceFilePath = Path.Combine(file.Name, rom.FullName.Replace(file.FullName, "").TrimStart('\\')),
                            SourceBaseDir = Path.Combine(file.Name),
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
                            InstallSize = (ulong)dirEnumerator.Where(f => !f.Attributes.HasFlag(FileAttributes.Directory)).Select(f => new FileInfo(f.FullName)).Sum(f => f.Length),
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

            if (!game.GameId.Contains(".") || !game.GameId.Contains("|"))
            {
                gameInfo = null;
                return false;
            }

            var playAction = game.GameActions.Where(ga => ga.IsPlayAction).First();
            if (mapping.RomType != RomType.MultiFile)
            {
                gameInfo = null;
                return false;
            }

            var parts = game.GameId.Split('|');

            Debug.Assert(parts.Length == 2, $"GameId is not in expected format (expected 2 parts, got {parts.Length})");

            if (string.IsNullOrEmpty(parts[0]))
            {
                gameInfo = null;
                return false;

            }

            gameInfo = new RomTypes.MultiFile.MultiFileGameInfo()
            {
                MappingId = mapping.MappingId,
                SourceFilePath = parts[1].Replace(mapping.SourcePath, "").TrimStart('\\'),
                SourceBaseDir = parts[0] == "*" ? Path.GetDirectoryName(parts[1]) : parts[0].Replace(mapping.SourcePath, "").TrimStart('\\'),
            };

            return true;
        }

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct)
        {
            return _playniteAPI.Database.Games.TakeWhile(g => !ct.IsCancellationRequested)
                .Where(g =>
            {
                if (g.PluginId != EmuLibrary.PluginId || g.IsInstalled)
                    return false;

                var info = g.GetELGameInfo();
                if (info.RomType != RomType.MultiFile)
                    return false;

                return !Directory.Exists((info as MultiFileGameInfo).SourceFullBaseDir);
            });
        }
    }
}
