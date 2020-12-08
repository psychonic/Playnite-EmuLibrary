using ROMManager.PlayniteCommon;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace ROMManager
{
    public class ROMManager : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private ROMInstallerSettings settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("41e49490-0583-4148-94d2-940c7c74f1d9");

        // Change to something more appropriate
        public override string Name => "ROM Manager";

        public ROMManager(IPlayniteAPI api) : base(api)
        {
            PlayniteAPI = api;
            settings = new ROMInstallerSettings(this, PlayniteAPI);
        }

        internal readonly IPlayniteAPI PlayniteAPI;

        public override IEnumerable<GameInfo> GetGames()
        {
#if false
            logger.Info($"Looking for games in {path}, using {profile.Name} emulator profile.");
            if (!profile.ImageExtensions.HasNonEmptyItems())
            {
                throw new Exception("Cannot scan for games, emulator doesn't support any file types.");
            }
#endif
            // TODO: nested folder support

            var games = new List<GameInfo>();

            settings.Mappings?.ToList().ForEach(mapping =>
            {
            var emulator = PlayniteAPI.Database.Emulators.First(e => e.Id == mapping.EmulatorId);
            var emuProfile = emulator.Profiles.First(p => p.Id == mapping.EmulatorProfileId);
            var platform = PlayniteAPI.Database.Platforms.First(p => p.Id == mapping.PlatformId);
            var imageExtensionsLower = emuProfile.ImageExtensions.Where(ext => !ext.IsNullOrEmpty()).Select(ext => ext.Trim().ToLower());
            var srcPath = mapping.SourcePath;
            var dstPath = mapping.DestinationPath;
            SafeFileEnumerator fileEnumerator;

            if (Directory.Exists(dstPath))
            {
                #region Import "installed" games
                fileEnumerator = new SafeFileEnumerator(dstPath, "*.*", SearchOption.TopDirectoryOnly);

                foreach (var file in fileEnumerator)
                {
                    if (mapping.GamesUseFolders && file.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        var rom = new SafeFileEnumerator(file.FullName, "*.*", SearchOption.AllDirectories).FirstOrDefault(f => imageExtensionsLower.Contains(f.Extension.TrimStart('.').ToLower()));
                        if (rom != null)
                        {
                            var newGame = new GameInfo()
                            {
                                Source = "ROM Manager",
                                Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                GameImagePath = rom.FullName,
                                InstallDirectory = file.FullName,
                                IsInstalled = true,
                                GameId = new RMPathInfo(new FileInfo(Path.Combine(Path.Combine(mapping.SourcePath, file.Name), rom.Name)), true).ToGameId(),
                                Platform = platform.Name,
                                PlayAction = new GameAction()
                                {
                                    Type = GameActionType.Emulator,
                                    EmulatorId = emulator.Id,
                                    EmulatorProfileId = emuProfile.Id,
                                    IsHandledByPlugin = false, // don't change this. PN will using emulator action
                                }
                            };

                            games.Add(newGame);
                        }
                    }
                    else if (!mapping.GamesUseFolders)
                    {
                        foreach (var extension in imageExtensionsLower)
                        {
                            if (file.Extension.TrimStart('.') == extension)
                            {
                                var newGame = new GameInfo()
                                {
                                    Source = "ROM Manager",
                                    Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                    GameImagePath = file.FullName,
                                    InstallDirectory = dstPath,
                                    IsInstalled = true,
                                    GameId = new RMPathInfo(new FileInfo(Path.Combine(mapping.SourcePath, file.Name)), false).ToGameId(),
                                    Platform = platform.Name,
                                    PlayAction = new GameAction()
                                    {
                                        Type = GameActionType.Emulator,
                                        EmulatorId = emulator.Id,
                                        EmulatorProfileId = emuProfile.Id,
                                        IsHandledByPlugin = false, // don't change this. PN will using emulator action
                                    }
                                };

                                games.Add(newGame);
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
                        if (mapping.GamesUseFolders && file.Attributes.HasFlag(FileAttributes.Directory))
                        {
                            var rom = new SafeFileEnumerator(file.FullName, "*.*", SearchOption.AllDirectories).FirstOrDefault(f => imageExtensionsLower.Contains(f.Extension.TrimStart('.').ToLower()));
                            if (rom != null)
                            {
                                var pathInfo = new RMPathInfo(new FileInfo(rom.FullName), true);
                                var equivalentInstalledPath = Path.Combine(dstPath, pathInfo.RelativeRomPath);
                                if (File.Exists(equivalentInstalledPath))
                                {
                                    continue;
                                }

                                var newGame = new GameInfo()
                                {
                                    Source = "ROM Manager",
                                    Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                    IsInstalled = false,
                                    GameId = pathInfo.ToGameId(),
                                    Platform = platform.Name,
                                    PlayAction = new GameAction()
                                    {
                                        Type = GameActionType.Emulator,
                                        EmulatorId = emulator.Id,
                                        EmulatorProfileId = emuProfile.Id,
                                        IsHandledByPlugin = false, // don't change this. PN will using emulator action
                                    }
                                };

                                games.Add(newGame);
                            }
                        }
                        else if (!mapping.GamesUseFolders)
                        {

                            foreach (var extension in imageExtensionsLower)
                            {
                                if (file.Extension.TrimStart('.') == extension)
                                {
                                    var equivalentInstalledPath = Path.Combine(dstPath, file.Name);
                                    if (File.Exists(equivalentInstalledPath))
                                    {
                                        continue;
                                    }

                                    var newGame = new GameInfo()
                                    {
                                        Source = "ROM Manager",
                                        Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                        IsInstalled = false,
                                        GameId = new RMPathInfo(new FileInfo(file.FullName), false).ToGameId(),
                                        Platform = platform.Name,
                                        PlayAction = new GameAction()
                                        {
                                            Type = GameActionType.Emulator,
                                            EmulatorId = emulator.Id,
                                            EmulatorProfileId = emuProfile.Id,
                                            IsHandledByPlugin = false, // don't change this. PN will using emulator action
                                        }
                                    };

                                    games.Add(newGame);
                                }
                            }
                        }
                    }
                }
            });
            #endregion

            return games;
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new ROMInstallerSettingsView();
        }

        public override IGameController GetGameController(Game game)
        {
            return new ROMManagerController(game, settings, PlayniteAPI);
        }

        public override List<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new List<MainMenuItem>()
            {
                new MainMenuItem()
                {
                    Action = (arags) => RemoveSuperUninstalledGames(),
                    Description = "Remove Entries With Missing Source ROM...",
                    MenuSection = "ROM Manager"
                }
            };
        }

        private void RemoveSuperUninstalledGames()
        {
            var onDiskFiles = new HashSet<string>(settings.Mappings?.SelectMany(mapping =>
            {
                var emulator = PlayniteAPI.Database.Emulators.First(e => e.Id == mapping.EmulatorId);
                var emuProfile = emulator.Profiles.First(p => p.Id == mapping.EmulatorProfileId);
                var imageExtensionsLower = emuProfile.ImageExtensions.Where(e => !e.IsNullOrEmpty() ).Select(e => e.Trim().ToLower());

                var srcPath = mapping.SourcePath;
                var dstPath = mapping.DestinationPath;

                return new SafeFileEnumerator(srcPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => {
                        return (!f.Attributes.HasFlag(FileAttributes.Directory))
                            && imageExtensionsLower.Contains(f.Extension.TrimStart('.').ToLower())
                            && !File.Exists(Path.Combine(dstPath, f.Name));
                        })
                    .Select(f => f.FullName);
            }));

            var toRemove = PlayniteApi.Database.Games.Where(g => g.PluginId == this.Id && !g.IsInstalled && !onDiskFiles.Contains(new RMPathInfo(g).SourceRomFile.FullName)).ToList();
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