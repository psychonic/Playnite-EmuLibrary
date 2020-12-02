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
                var imageExtensions = emuProfile.ImageExtensions;
                var srcPath = mapping.SourcePath;
                var dstPath = mapping.DestinationPath;

                #region Import "installed" games
                var fileEnumerator = new SafeFileEnumerator(dstPath, "*.*", SearchOption.TopDirectoryOnly /*SearchOption.AllDirectories*/);

                foreach (var file in fileEnumerator)
                {
                    if (file.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        continue;
                    }

                    foreach (var extension in imageExtensions)
                    {
                        if (extension.IsNullOrEmpty())
                        {
                            continue;
                        }

                        if (string.Equals(file.Extension.TrimStart('.'), extension.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            var newGame = new GameInfo()
                            {
                                Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                GameImagePath = file.FullName,
                                InstallDirectory = dstPath,
                                IsInstalled = true,
                                GameId = Path.Combine(srcPath, file.Name),
                                Platform = platform.Name,
                                PlayAction = new GameAction()
                                {
                                    Type = GameActionType.Emulator,
                                    EmulatorId = emulator.Id,
                                    EmulatorProfileId = emuProfile.Id,
                                    IsHandledByPlugin = true,
                                }
                            };

                            games.Add(newGame);
                        }
                    }
                }
                #endregion

                #region Import "uninstalled" games
                fileEnumerator = new SafeFileEnumerator(srcPath, "*.*", SearchOption.TopDirectoryOnly /*SearchOption.AllDirectories*/);

                foreach (var file in fileEnumerator)
                {
                    if (file.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        continue;
                    }

                    foreach (var extension in imageExtensions)
                    {
                        if (extension.IsNullOrEmpty())
                        {
                            continue;
                        }

                        if (string.Equals(file.Extension.TrimStart('.'), extension.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            var equivalentInstalledPath = Path.Combine(dstPath, file.Name);
                            if (File.Exists(equivalentInstalledPath))
                            {
                                continue;
                            }

                            var newGame = new GameInfo()
                            {
                                Name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name))),
                                IsInstalled = false,
                                GameId = file.FullName,
                                Platform = platform.Name,
                                PlayAction = new GameAction()
                                {
                                    Type = GameActionType.Emulator,
                                    EmulatorId = emulator.Id,
                                    EmulatorProfileId = emuProfile.Id,
                                    IsHandledByPlugin = true,
                                }
                            };

                            games.Add(newGame);
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

                return new SafeFileEnumerator(srcPath, "*.*", SearchOption.TopDirectoryOnly /*SearchOption.AllDirectories*/)
                    .Where(f => {
                        return (!f.Attributes.HasFlag(FileAttributes.Directory))
                            && imageExtensionsLower.Contains(f.Extension.TrimStart('.').ToLower())
                            && !File.Exists(Path.Combine(dstPath, f.Name));
                        })
                    .Select(f => f.FullName);
            }));

            var toRemove = PlayniteApi.Database.Games.Where(g => g.PluginId == this.Id && !g.IsInstalled && !onDiskFiles.Contains(g.GameId)).ToList();
            var res = PlayniteApi.Dialogs.ShowMessage(string.Format("Delete {0} library entries?", toRemove.Count), "Confirm deletion", System.Windows.MessageBoxButton.YesNo);
            if (res == System.Windows.MessageBoxResult.Yes)
            {
                PlayniteApi.Database.Games.Remove(toRemove);
            }
        }
    }
}