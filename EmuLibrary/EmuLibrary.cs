using EmuLibrary.RomTypes;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ProtoBuf.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;

namespace EmuLibrary
{
    public class EmuLibrary : LibraryPlugin, IEmuLibrary
    {
        // LibraryPlugin fields
        public override Guid Id { get; } = PluginId;
        public override string Name => s_pluginName;
        public override string LibraryIcon => Icon;

        // IEmuLibrary fields
        public ILogger Logger => LogManager.GetLogger();
        public IPlayniteAPI Playnite { get; private set; }
        public EmuLibrarySettings Settings { get; private set; }
        RomTypeScanner IEmuLibrary.GetScanner(RomType romType) => _scanners[romType];

        private const string s_pluginName = "EmuLibrary";

        internal static readonly string Icon = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"icon.png");
        internal static readonly Guid PluginId = Guid.Parse("41e49490-0583-4148-94d2-940c7c74f1d9");
        internal static readonly MetadataNameProperty SourceName = new MetadataNameProperty(s_pluginName);

        private readonly Dictionary<RomType, RomTypeScanner> _scanners = new Dictionary<RomType, RomTypeScanner>();

        public EmuLibrary(IPlayniteAPI api) : base(api)
        {
            Playnite = api;
            Settings = new EmuLibrarySettings(this, Playnite);

            var romTypes = Enum.GetValues(typeof(RomType)).Cast<RomType>();
            foreach (var rt in romTypes)
            {
                var fieldInfo = rt.GetType().GetField(rt.ToString());
                var romInfo = fieldInfo.GetCustomAttributes(false).OfType<RomTypeInfoAttribute>().FirstOrDefault();
                if (romInfo == null)
                {
                    Logger.Warn($"Failed to find {nameof(RomTypeInfoAttribute)} for RomType {rt}. Skipping...");
                    continue;
                }

                // Hook up ProtoInclude on ELGameInfo for each RomType
                // Starts at field number 10 to not conflict with ELGameInfo's fields
                RuntimeTypeModel.Default[typeof(ELGameInfo)].AddSubType((int)rt + 10, romInfo.GameInfoType);

                var scanner = romInfo.ScannerType.GetConstructor(new Type[] { typeof(IEmuLibrary) })?.Invoke(new object[] { Playnite });
                if (scanner == null)
                {
                    Logger.Error($"Failed to instantiate scanner for RomType {rt} (using {romInfo.ScannerType}).");
                    continue;
                }

                _scanners.Add(rt, scanner as RomTypeScanner);
            }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);

            Settings.Mappings.ForEach(mapping =>
            {
                _scanners.Values.ForEach(scanner =>
                {
                    var oldGameIdFormat = PlayniteApi.Database.Games.Where(game => game.PluginId == scanner.LegacyPluginId && !game.GameId.StartsWith("!"));
                    if (oldGameIdFormat.Any())
                    {
                        Logger.Info($"Updating {oldGameIdFormat.Count()} games to new game id format for mapping {mapping.MappingId} ({mapping.Emulator.Name}/{mapping.EmulatorProfile.Name}/{mapping.SourcePath}).");
                        using (Playnite.Database.BufferedUpdate())
                        {
                            oldGameIdFormat.ForEach(game =>
                            {
                                if (scanner.TryGetGameInfoBaseFromLegacyGameId(game, mapping, out var gameInfo))
                                {
                                    game.GameId = gameInfo.AsGameId();
                                    game.PluginId = PluginId;
                                    PlayniteApi.Database.Games.Update(game);
                                }
                            });
                        }
                    }
                });
            });
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            if (Playnite.ApplicationInfo.Mode == ApplicationMode.Fullscreen && !Settings.ScanGamesInFullScreen)
            {
                yield break;
            }

            foreach (var mapping in Settings.Mappings?.Where(m => m.Enabled))
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                if (mapping.Emulator == null)
                {
                    Logger.Warn($"Emulator {mapping.EmulatorId} not found, skipping.");
                    continue;
                }

                if (mapping.EmulatorProfile == null)
                {
                    Logger.Warn($"Emulator profile {mapping.EmulatorProfileId} for emulator {mapping.EmulatorId} not found, skipping.");
                    continue;
                }

                if (mapping.Platform == null)
                {
                    Logger.Warn($"Platform {mapping.PlatformId} not found, skipping.");
                    continue;
                }

                if (!_scanners.TryGetValue(mapping.RomType, out RomTypeScanner scanner))
                {
                    Logger.Warn($"Rom type {mapping.RomType} not supported, skipping.");
                    continue;
                }

                foreach (var g in scanner.GetGames(mapping, args))
                {
                    yield return g;
                }
            }

            if (Settings.AutoRemoveUninstalledGamesMissingFromSource)
            {
                RemoveSuperUninstalledGames(false);
            }
        }

        public override ISettings GetSettings(bool firstRunSettings) => Settings;
        public override UserControl GetSettingsView(bool firstRunSettings) => new EmuLibrarySettingsView();

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                yield return args.Game.GetELGameInfo().GetInstallController(args.Game, this);
            }
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                yield return args.Game.GetELGameInfo().GetUninstallController(args.Game, this);
            }
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            base.OnGameInstalled(args);

            if (args.Game.PluginId == PluginId && Settings.NotifyOnInstallComplete)
            {
                Playnite.Notifications.Add(args.Game.GameId, $"Installation of \"{args.Game.Name}\" has completed", NotificationType.Info);
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem()
            {
                Action = (arags) => RemoveSuperUninstalledGames(true),
                Description = "Remove uninstalled games with missing source file...",
                MenuSection = "EmuLibrary"
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var ourGames = args.Games.Where(g => g.PluginId == Id);
            if (ourGames.Any())
            {
                var text = ourGames.Select(g => g.GetELGameInfo().ToDescriptiveString(g))
                    .Aggregate((a, b) => $"{a}\n--------------------------------------------------------------------\n{b}");

                yield return new GameMenuItem()
                {
                    Action = (arags) => Playnite.Dialogs.ShowSelectableString("Decoded GameId info for each selected game is shown below. This information can be useful for troubleshooting.", "EmuLibrary Game Info", text),
                    Description = "Show Debug Info",
                    MenuSection = "EmuLibrary"
                };
            }

            foreach (var gmi in base.GetGameMenuItems(args))
            {
                yield return gmi;
            }
        }

        private void RemoveSuperUninstalledGames(bool promptUser)
        {
            var toRemove = _scanners.Values.SelectMany(s => s.GetUninstalledGamesMissingSourceFiles());
            if (toRemove.Any())
            {
                System.Windows.MessageBoxResult res;
                if (promptUser)
                {
                    res = PlayniteApi.Dialogs.ShowMessage($"Delete {toRemove.Count()} library entries?", "Confirm deletion", System.Windows.MessageBoxButton.YesNo);
                }
                else
                {
                    res = System.Windows.MessageBoxResult.Yes;
                }

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