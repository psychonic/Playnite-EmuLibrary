using EmuLibrary.PlayniteCommon;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Controls;

namespace EmuLibrary
{
    public class EmuLibrary : LibraryPlugin
    {
        private static readonly string PluginName = "EmuLibrary";
        private static readonly ILogger logger = LogManager.GetLogger();

        private EmuLibrarySettings settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("41e49490-0583-4148-94d2-940c7c74f1d9");

        // Change to something more appropriate
        public override string Name => PluginName;

        private static readonly MetadataNameProperty SourceName = new MetadataNameProperty(PluginName);

        public EmuLibrary(IPlayniteAPI api) : base(api)
        {
            PlayniteAPI = api;
            settings = new EmuLibrarySettings(this, PlayniteAPI);
        }

        internal readonly IPlayniteAPI PlayniteAPI;

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
#if false
            logger.Info($"Looking for games in {path}, using {profile.Name} emulator profile.");
            if (!profile.ImageExtensions.HasNonEmptyItems())
            {
                throw new Exception("Cannot scan for games, emulator doesn't support any file types.");
            }
#endif

            if (PlayniteAPI.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
            {
                yield break;
            }

            if (args.CancelToken.IsCancellationRequested)
                yield break;

            // Hack to exclude anything past disc one for games we're not treating as multi-file / m3u but have multiple discs :|
            var discXpattern = new Regex(@"\(Disc \d", RegexOptions.Compiled);

            foreach (var mapping in settings.Mappings?.Where(m => m.Enabled))
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;
                
                var emulator = PlayniteAPI.Database.Emulators.First(e => e.Id == mapping.EmulatorId);
                var emuProfile = emulator.AllProfiles.First(p => p.Id == mapping.EmulatorProfileId);
                var platform = PlayniteAPI.Emulation.Platforms.First(p => p.Id == mapping.PlatformId);
                var imageExtensionsLower = PlayniteAPI.Emulation.Emulators.First(e => e.Id == emulator.BuiltInConfigId).Profiles.First(p => p.Name == emuProfile.Name).ImageExtensions.Where(ext => !ext.IsNullOrEmpty()).Select(ext => ext.Trim().ToLower());
                var srcPath = mapping.SourcePath;
                var dstPath = mapping.DestinationPathResolved;
                SafeFileEnumerator fileEnumerator;

                if (Directory.Exists(dstPath))
                {
                    #region Import "installed" games
                    fileEnumerator = new SafeFileEnumerator(dstPath, "*.*", SearchOption.TopDirectoryOnly);

                    foreach (var file in fileEnumerator)
                    {
                        if (args.CancelToken.IsCancellationRequested)
                            yield break;
                        
                        if (mapping.GamesUseFolders && file.Attributes.HasFlag(FileAttributes.Directory) && !discXpattern.IsMatch(file.Name))
                        {
                            var rom = new SafeFileEnumerator(file.FullName, "*.*", SearchOption.AllDirectories).FirstOrDefault(f => imageExtensionsLower.Contains(f.Extension.TrimStart('.').ToLower()));
                            if (rom != null)
                            {
                                var gameName = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name)));
                                yield return new GameMetadata()
                                {
                                    Source = SourceName,
                                    Name = gameName,
                                    Roms = new List<GameRom>() { new GameRom(gameName, PlayniteApi.Paths.IsPortable ? rom.FullName.Replace(PlayniteApi.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : rom.FullName) },
                                    InstallDirectory = PlayniteApi.Paths.IsPortable ? file.FullName.Replace(PlayniteApi.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : file.FullName,
                                    IsInstalled = true,
                                    GameId = new ELPathInfo(new FileInfo(Path.Combine(new string[] { mapping.SourcePath, file.Name, rom.FullName.Replace(file.FullName, "").TrimStart('\\') })), new DirectoryInfo(Path.Combine(mapping.SourcePath, file.Name))).ToGameId(),
                                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(platform.Name) },
                                    GameActions = new List<GameAction>() { new GameAction()
                                    {
                                        Name = $"Play in {emulator.Name}",
                                        Type = GameActionType.Emulator,
                                        EmulatorId = emulator.Id,
                                        EmulatorProfileId = emuProfile.Id,
                                        IsPlayAction = true,
                                    } }
                                };
                            }
                        }
                        else if (!mapping.GamesUseFolders)
                        {
                            foreach (var extension in imageExtensionsLower)
                            {
                                if (args.CancelToken.IsCancellationRequested)
                                    yield break;
                                
                                if (file.Extension.TrimStart('.') == extension && !discXpattern.IsMatch(file.Name))
                                {
                                    var gameName = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name)));
                                    yield return new GameMetadata()
                                    {
                                        Source = SourceName,
                                        Name = gameName,
                                        Roms = new List<GameRom>() { new GameRom(gameName, PlayniteApi.Paths.IsPortable ? file.FullName.Replace(PlayniteApi.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : file.FullName) },
                                        InstallDirectory = PlayniteApi.Paths.IsPortable ? dstPath.Replace(PlayniteApi.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory) : dstPath,
                                        IsInstalled = true,
                                        GameId = new ELPathInfo(new FileInfo(Path.Combine(mapping.SourcePath, file.Name))).ToGameId(),
                                        Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(platform.Name) },
                                        GameActions = new List<GameAction>() { new GameAction()
                                        {
                                            Name = $"Play in {emulator.Name}",
                                            Type = GameActionType.Emulator,
                                            EmulatorId = emulator.Id,
                                            EmulatorProfileId = emuProfile.Id,
                                            IsPlayAction = true,
                                        } }
                                    };
                                }
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
                        
                        if (mapping.GamesUseFolders && file.Attributes.HasFlag(FileAttributes.Directory) && !discXpattern.IsMatch(file.Name))
                        {
                            var rom = new SafeFileEnumerator(file.FullName, "*.*", SearchOption.AllDirectories).FirstOrDefault(f => imageExtensionsLower.Contains(f.Extension.TrimStart('.').ToLower()));
                            if (rom != null)
                            {
                                var pathInfo = new ELPathInfo(new FileInfo(rom.FullName), new DirectoryInfo(file.FullName));
                                var equivalentInstalledPath = Path.Combine(dstPath, pathInfo.RelativeRomPath);
                                if (File.Exists(equivalentInstalledPath))
                                {
                                    continue;
                                }

                                yield return new GameMetadata()
                                {
                                    Source = SourceName,
                                    Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                    IsInstalled = false,
                                    GameId = pathInfo.ToGameId(),
                                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(platform.Name) },
                                    GameActions = new List<GameAction>() { new GameAction()
                                    {
                                        Name = $"Play in {emulator.Name}",
                                        Type = GameActionType.Emulator,
                                        EmulatorId = emulator.Id,
                                        EmulatorProfileId = emuProfile.Id,
                                        IsPlayAction = true,
                                    } }
                                };
                            }
                        }
                        else if (!mapping.GamesUseFolders)
                        {

                            foreach (var extension in imageExtensionsLower)
                            {
                                if (args.CancelToken.IsCancellationRequested)
                                    yield break;
                                
                                if (file.Extension.TrimStart('.') == extension && !discXpattern.IsMatch(file.Name))
                                {
                                    var equivalentInstalledPath = Path.Combine(dstPath, file.Name);
                                    if (File.Exists(equivalentInstalledPath))
                                    {
                                        continue;
                                    }

                                    yield return new GameMetadata()
                                    {
                                        Source = SourceName,
                                        Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                        IsInstalled = false,
                                        GameId = new ELPathInfo(new FileInfo(file.FullName)).ToGameId(),
                                        Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(platform.Name) },
                                        GameActions = new List<GameAction>() { new GameAction()
                                        {
                                            Name = $"Play in {emulator.Name}",
                                            Type = GameActionType.Emulator,
                                            EmulatorId = emulator.Id,
                                            EmulatorProfileId = emuProfile.Id,
                                            IsPlayAction = true,
                                        } }
                                    };
                                }
                            }
                        }
                    }
                }
            }
            #endregion
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new EmuLibrarySettingsView();
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                yield return new EmuLibraryInstallController(args.Game, settings, PlayniteAPI);
            }
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                yield return new EmuLibraryUninstallController(args.Game, PlayniteAPI);
            }            
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem()
            {
                Action = (arags) => RemoveSuperUninstalledGames(),
                Description = "Remove Entries With Missing Source ROM...",
                MenuSection = "EmuLibrary"
            };
        }

        private void RemoveSuperUninstalledGames()
        {
            var onDiskFiles = new HashSet<string>(settings.Mappings?.SelectMany(mapping =>
            {
                var emulator = PlayniteAPI.Database.Emulators.First(e => e.Id == mapping.EmulatorId);
                var emuProfile = emulator.SelectableProfiles.First(p => p.Id == mapping.EmulatorProfileId);
                var imageExtensionsLower = PlayniteAPI.Emulation.Emulators.First(e => e.Id == emulator.BuiltInConfigId).Profiles.First(p => p.Name == emuProfile.Name).ImageExtensions.Where(ext => !ext.IsNullOrEmpty()).Select(ext => ext.Trim().ToLower());

                var srcPath = mapping.SourcePath;
                var dstPath = mapping.DestinationPathResolved;

                return new SafeFileEnumerator(srcPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => {
                        return (!f.Attributes.HasFlag(FileAttributes.Directory))
                            && imageExtensionsLower.Contains(f.Extension.TrimStart('.').ToLower())
                            && !File.Exists(Path.Combine(dstPath, f.Name));
                        })
                    .Select(f => f.FullName);
            }));

            var toRemove = PlayniteApi.Database.Games.Where(g => g.PluginId == this.Id && !g.IsInstalled && !onDiskFiles.Contains(new ELPathInfo(g).SourceRomFile.FullName)).ToList();
            if (toRemove.Count > 0)
            {
                var res = PlayniteApi.Dialogs.ShowMessage(string.Format("Delete {0} library entries?", toRemove.Count), "Confirm deletion", System.Windows.MessageBoxButton.YesNo);
                if (res == System.Windows.MessageBoxResult.Yes)
                {
                    PlayniteApi.Database.Games.Remove(toRemove);
                }
            }
            else
            {
                PlayniteApi.Dialogs.ShowMessage("Nothing to do.");
            }
        }
    }
}