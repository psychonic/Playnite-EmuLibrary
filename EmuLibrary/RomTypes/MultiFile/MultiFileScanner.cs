using EmuLibrary.PlayniteCommon;
using EmuLibrary.RomTypes.SingleFile;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmuLibrary.RomTypes.MultiFile
{
    internal class MultiFileScanner : RomTypeScanner
    {
        private readonly IPlayniteAPI _playniteAPI;

        // Hack to exclude anything past disc one for games we're not treating as multi-file / m3u but have multiple discs :|
        static private readonly Regex s_discXpattern = new Regex(@"\((?:Disc|Disk) \d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public override RomType RomType => RomType.MultiFile;
        public override Guid LegacyPluginId => EmuLibrary.PluginId;

        public MultiFileScanner(IPlayniteAPI api) : base(api)
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

                    if (file.Attributes.HasFlag(FileAttributes.Directory) && !s_discXpattern.IsMatch(file.Name))
                    {
                        var dirEnumerator = new SafeFileEnumerator(file.FullName, "*.*", SearchOption.AllDirectories);
                        // First matching rom of first valid extension that has any matches. Ex. for "m3u,cue,bin", make sure we don't grab a bin file when there's an m3u or cue handy
                        var rom = imageExtensionsLower.Select(ext => dirEnumerator.FirstOrDefault(f => f.Extension.TrimStart('.').ToLower() == ext)).FirstOrDefault(f => f != null);
                        if (rom != null)
                        {
                            var gameName = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name)));
                            var info = new MultiFileGameInfo()
                            {
                                MappingId = mapping.MappingId,

                                // Relative to mapping.SourcePath
                                SourceFilePath = Path.Combine(file.Name, rom.FullName.Replace(file.FullName, "").TrimStart('\\')),
                                SourceBaseDir = Path.Combine(file.Name),
                            };

                            yield return new GameMetadata()
                            {
                                Source = EmuLibrary.SourceName,
                                Name = gameName,
                                Roms = new List<GameRom>() { new GameRom(gameName, _playniteAPI.Paths.IsPortable ? rom.FullName.Replace(_playniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : rom.FullName) },
                                InstallDirectory = _playniteAPI.Paths.IsPortable ? file.FullName.Replace(_playniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : file.FullName,
                                IsInstalled = true,
                                GameId = info.AsGameId(),
                                Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                                InstallSize = (ulong)dirEnumerator.Where(f => !f.Attributes.HasFlag(FileAttributes.Directory)).Select(f => new FileInfo(f.FullName)).Sum(f => f.Length),
                                GameActions = new List<GameAction>() { new GameAction()
                                    {
                                        Name = $"Play in {mapping.Emulator.Name}",
                                        Type = GameActionType.Emulator,
                                        EmulatorId = mapping.EmulatorId,
                                        EmulatorProfileId = mapping.EmulatorProfileId,
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

                    if (file.Attributes.HasFlag(FileAttributes.Directory) && !s_discXpattern.IsMatch(file.Name))
                    {
                        var dirEnumerator = new SafeFileEnumerator(file.FullName, "*.*", SearchOption.AllDirectories);
                        // First matching rom of first valid extension that has any matches. Ex. for "m3u,cue,bin", make sure we don't grab a bin file when there's an m3u or cue handy
                        var rom = imageExtensionsLower.Select(ext => dirEnumerator.FirstOrDefault(f => f.Extension.TrimStart('.').ToLower() == ext)).FirstOrDefault(f => f != null);
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

                            yield return new GameMetadata()
                            {
                                Source = EmuLibrary.SourceName,
                                Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                IsInstalled = false,
                                GameId = info.AsGameId(),
                                Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                                InstallSize = (ulong)dirEnumerator.Where(f => !f.Attributes.HasFlag(FileAttributes.Directory)).Select(f => new FileInfo(f.FullName)).Sum(f => f.Length),
                                GameActions = new List<GameAction>() { new GameAction()
                                    {
                                        Name = $"Play in {mapping.Emulator.Name}",
                                        Type = GameActionType.Emulator,
                                        EmulatorId = mapping.EmulatorId,
                                        EmulatorProfileId = mapping.EmulatorProfileId,
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

            if (!game.GameId.Contains(".") || !game.GameId.Contains("|"))
            {
                gameInfo = null;
                return false;
            }

            var playAction = game.GameActions.Where(ga => ga.IsPlayAction).First();
            var mapping = EmuLibrarySettings.Instance.Mappings.FirstOrDefault(m => m.EmulatorId == playAction.EmulatorId && m.EmulatorProfileId == playAction.EmulatorProfileId);
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

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles()
        {
            return _playniteAPI.Database.Games.Where(g =>
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
