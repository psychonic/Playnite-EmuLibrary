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
    public class EmuLibrary : LibraryPlugin
    {
        private const string s_pluginName = "EmuLibrary";
        private static readonly ILogger s_logger = LogManager.GetLogger();

        internal static readonly string Icon = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"icon.png");
        internal static readonly Guid PluginId = Guid.Parse("41e49490-0583-4148-94d2-940c7c74f1d9");
        internal static readonly MetadataNameProperty SourceName = new MetadataNameProperty(s_pluginName);

        private readonly IPlayniteAPI _playniteAPI;
        private readonly EmuLibrarySettings _settings;
        private readonly Dictionary<RomType, RomTypeScanner> _scanners = new Dictionary<RomType, RomTypeScanner>();

        public override Guid Id { get; } = PluginId;
        public override string Name => s_pluginName;
        public override string LibraryIcon => Icon;

        public EmuLibrary(IPlayniteAPI api) : base(api)
        {
            _playniteAPI = api;
            _settings = new EmuLibrarySettings(this, _playniteAPI);

            var romTypes = Enum.GetValues(typeof(RomType)).Cast<RomType>();
            foreach (var rt in romTypes)
            {
                var fieldInfo = rt.GetType().GetField(rt.ToString());
                var romInfo = fieldInfo.GetCustomAttributes(false).OfType<RomTypeInfoAttribute>().FirstOrDefault();
                if (romInfo == null)
                    continue;

                // Hook up ProtoInclude on ELGameInfo for each RomType
                // Starts at field number 10 to not conflict with ELGameInfo's fields
                RuntimeTypeModel.Default[typeof(ELGameInfo)].AddSubType((int)rt + 10, romInfo.GameInfoType);

                var scanner = romInfo.ScannerType.GetConstructor(new Type[] { typeof(IPlayniteAPI) })?.Invoke(new object[] { _playniteAPI });
                _scanners.Add(rt, scanner as RomTypeScanner);
            }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);

            _scanners.Values.ForEach(h =>
            {
                var oldGameIdFormat = PlayniteApi.Database.Games.Where(g => g.PluginId == h.LegacyPluginId && !g.GameId.StartsWith("!"));
                if (oldGameIdFormat.Any())
                {
                    s_logger.Info($"Updating {oldGameIdFormat.Count()} games to new game id format.");
                    using (_playniteAPI.Database.BufferedUpdate())
                    {
                        oldGameIdFormat.ForEach(g =>
                        {
                            if (h.TryGetGameInfoBaseFromLegacyGameId(g, out var gameInfo))
                            {
                                g.GameId = gameInfo.AsGameId();
                                PlayniteApi.Database.Games.Update(g);
                            }
                        });
                    }
                }
            });
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            if (_playniteAPI.ApplicationInfo.Mode == ApplicationMode.Fullscreen && !_settings.ScanGamesInFullScreen)
            {
                yield break;
            }

            foreach (var mapping in _settings.Mappings?.Where(m => m.Enabled))
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                if (mapping.Emulator == null)
                {
                    s_logger.Warn($"Emulator {mapping.EmulatorId} not found, skipping.");
                    continue;
                }

                if (mapping.EmulatorProfile == null)
                {
                    s_logger.Warn($"Emulator profile {mapping.EmulatorProfileId} for emulator {mapping.EmulatorId} not found, skipping.");
                    continue;
                }

                if (mapping.Platform == null)
                {
                    s_logger.Warn($"Platform {mapping.PlatformId} not found, skipping.");
                    continue;
                }

                if (!_scanners.TryGetValue(mapping.RomType, out RomTypeScanner scanner))
                {
                    s_logger.Warn($"Rom type {mapping.RomType} not supported, skipping.");
                    continue;
                }

                foreach (var g in scanner.GetGames(mapping, args))
                {
                    yield return g;
                }
            }
        }

        public override ISettings GetSettings(bool firstRunSettings) => _settings;
        public override UserControl GetSettingsView(bool firstRunSettings) => new EmuLibrarySettingsView();

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                yield return args.Game.GetELGameInfo().GetInstallController(args.Game, _settings, _playniteAPI);
            }
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                yield return args.Game.GetELGameInfo().GetUninstallController(args.Game, _playniteAPI);
            }
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            base.OnGameInstalled(args);

            if (args.Game.PluginId == PluginId && _settings.NotifyOnInstallComplete)
            {
                _playniteAPI.Notifications.Add(args.Game.GameId, $"Installation of \"{args.Game.Name}\" has completed", NotificationType.Info);
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem()
            {
                Action = (arags) => RemoveSuperUninstalledGames(),
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
                    Action = (arags) => _playniteAPI.Dialogs.ShowSelectableString("Decoded GameId info for each selected game is shown below. This information can be useful for troubleshooting.", "EmuLibrary Game Info", text),
                    Description = "Show Debug Info",
                    MenuSection = "EmuLibrary"
                };
            }

            foreach (var gmi in base.GetGameMenuItems(args))
            {
                yield return gmi;
            }
        }

        private void RemoveSuperUninstalledGames()
        {
            var toRemove = _scanners.Values.SelectMany(s => s.GetUninstalledGamesMissingSourceFiles());
            if (toRemove.Any())
            {
                var res = PlayniteApi.Dialogs.ShowMessage($"Delete {toRemove.Count()} library entries?", "Confirm deletion", System.Windows.MessageBoxButton.YesNo);
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