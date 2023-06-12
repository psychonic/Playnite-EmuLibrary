using EmuLibrary.PlayniteCommon;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text.RegularExpressions;

namespace EmuLibrary.RomTypes.SingleFile
{
    internal class SingleFileScanner : RomTypeScanner
    {
        private readonly IPlayniteAPI _playniteAPI;

        // Hack to exclude anything past disc one for games we're not treating as multi-file / m3u but have multiple discs :|
        static private readonly Regex s_discXpattern = new Regex(@"\((?:Disc|Disk) \d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public override RomType RomType => RomType.SingleFile;
        public override Guid LegacyPluginId => EmuLibrary.PluginId;

        public SingleFileScanner(IPlayniteAPI api) : base(api)
        {
            _playniteAPI = api;
        }

        public override IEnumerable<GameMetadata> GetGames(EmuLibrarySettings.ROMInstallerEmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            var imageExtensionsLower = mapping.ImageExtensionsLower;
            var srcPath = mapping.SourcePath;
            var dstPath = mapping.DestinationPathResolved;
            SafeFileEnumerator fileEnumerator;

            #region Import "installed" games
            if (Directory.Exists(dstPath))
            {
                fileEnumerator = new SafeFileEnumerator(dstPath, "*.*", SearchOption.TopDirectoryOnly);

                foreach (var file in fileEnumerator)
                {
                    if (args.CancelToken.IsCancellationRequested)
                        yield break;

                    foreach (var extension in imageExtensionsLower)
                    {
                        if (args.CancelToken.IsCancellationRequested)
                            yield break;

                        if (file.Extension.TrimStart('.') == extension && !s_discXpattern.IsMatch(file.Name))
                        {
                            var gameName = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name)));
                            var info = new SingleFileGameInfo()
                            {
                                MappingId = mapping.MappingId,
                                SourcePath = file.Name,
                            };

                            yield return new GameMetadata()
                            {
                                Source = EmuLibrary.SourceName,
                                Name = gameName,
                                Roms = new List<GameRom>() { new GameRom(gameName, _playniteAPI.Paths.IsPortable ? file.FullName.Replace(_playniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : file.FullName) },
                                InstallDirectory = _playniteAPI.Paths.IsPortable ? dstPath.Replace(_playniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : dstPath,
                                IsInstalled = true,
                                GameId = info.AsGameId(),
                                Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                                InstallSize = (ulong)new FileInfo(file.FullName).Length,
                                GameActions = new List<GameAction>() { new GameAction()
                                {
                                    Name = $"Play in {mapping.Emulator.Name}",
                                    Type = GameActionType.Emulator,
                                    EmulatorId = mapping.Emulator.Id,
                                    EmulatorProfileId = mapping.EmulatorProfile.Id,
                                    IsPlayAction = true,
                                } }
                            };
                        }
                    }
                }
            }
            #endregion

            #region Import "uninstalled" games
            if (Directory.Exists(srcPath))
            {
                fileEnumerator = new SafeFileEnumerator(srcPath, "*.*", SearchOption.TopDirectoryOnly);

                foreach (var file in fileEnumerator)
                {
                    if (args.CancelToken.IsCancellationRequested)
                        yield break;

                    foreach (var extension in imageExtensionsLower)
                    {
                        if (args.CancelToken.IsCancellationRequested)
                            yield break;

                        if (file.Extension.TrimStart('.') == extension && !s_discXpattern.IsMatch(file.Name))
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

                            yield return new GameMetadata()
                            {
                                Source = EmuLibrary.SourceName,
                                Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                IsInstalled = false,
                                GameId = info.AsGameId(),
                                Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                                InstallSize = (ulong)new FileInfo(file.FullName).Length,
                                GameActions = new List<GameAction>() { new GameAction()
                                {
                                    Name = $"Play in {mapping.Emulator.Name}",
                                    Type = GameActionType.Emulator,
                                    EmulatorId = mapping.Emulator.Id,
                                    EmulatorProfileId = mapping.EmulatorProfile.Id,
                                    IsPlayAction = true,
                                } }
                            };
                        }
                    }
                }
            }
            #endregion
        }

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, out ELGameInfo gameInfo)
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
            var mapping = EmuLibrarySettings.Instance.Mappings.FirstOrDefault(m => m.EmulatorId == playAction.EmulatorId && m.EmulatorProfileId == playAction.EmulatorProfileId);
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

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles()
        {
            return _playniteAPI.Database.Games.Where(g =>
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
