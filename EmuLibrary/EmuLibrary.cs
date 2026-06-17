using EmuLibrary.RomTypes;
using EmuLibrary.Settings;
using EmuLibrary.Util.ScanCache;
using EmuLibrary.Util.ScanConcurrency;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        public Settings.Settings Settings { get; private set; }
        private IScanCache _scanCache;
        IScanCache IEmuLibrary.ScanCache => _scanCache;
        private IScanConcurrency _scanConcurrency;
        IScanConcurrency IEmuLibrary.ScanConcurrency => _scanConcurrency;
        RomTypeScanner IEmuLibrary.GetScanner(RomType romType) => _scanners[romType];

        private const string s_pluginName = "EmuLibrary";

        internal static readonly string Icon = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"icon.png");
        internal static readonly Guid PluginId = Guid.Parse("41e49490-0583-4148-94d2-940c7c74f1d9");
        internal static readonly MetadataNameProperty SourceName = new MetadataNameProperty(s_pluginName);

        private readonly Dictionary<RomType, RomTypeScanner> _scanners = new Dictionary<RomType, RomTypeScanner>();

        // Cross-mapping producer fan-out: how many mappings may *produce* GameMetadata at once. Per-device I/O
        // load is bounded separately by the ScanConcurrency governor (per-host + global permits), so this is no
        // longer a device-protection knob — it only lets mappings on *distinct* endpoints progress in parallel.
        // Capped at the governor's global ceiling; beyond it extra producers would just block on the shared
        // global permit.
        private const int MaxScanConcurrency = ScanConcurrencyGovernor.GlobalMax;

        // Bound on the producer->consumer hand-off queue so a fast scan can't buffer an unbounded number of
        // GameMetadata ahead of Playnite's consumption on a very large library.
        private const int ScanQueueCapacity = 256;

        public EmuLibrary(IPlayniteAPI api) : base(api)
        {
            Playnite = api;

            _scanCache = new JsonScanCache(
                Path.Combine(GetPluginUserDataPath(), "scancache.json"),
                Logger);

            _scanConcurrency = new ScanConcurrencyGovernor(Logger);

            // This must occur before we instantiate the Settings class
            InitializeRomTypeScanners();

            Settings = new Settings.Settings(this, this);
        }

        private void InitializeRomTypeScanners()
        {
            // Hook up ProtoInclude on ELGameInfo for each RomType (offset numbering lives there).
            ELGameInfo.RegisterProtoSubTypes();

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

                var scanner = romInfo.ScannerType.GetConstructor(new Type[] { typeof(IEmuLibrary) })?.Invoke(new object[] { this });
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

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var jobs = BuildScanJobs();
                Logger.Info($"Starting library scan of {jobs.Count} mapping(s) (up to {Math.Min(MaxScanConcurrency, Math.Max(jobs.Count, 1))} concurrent).");

                foreach (var g in ScanMappings(jobs, args))
                {
                    yield return g;
                }

                if (Settings.AutoRemoveUninstalledGamesMissingFromSource)
                {
                    RemoveSuperUninstalledGames(false, args.CancelToken);
                }
            }
            finally
            {
                _scanCache.Flush();
                stopwatch.Stop();
                Logger.Info($"Library scan finished in {stopwatch.Elapsed.TotalSeconds:F1}s.");
            }
        }

        // Resolves the enabled mappings we will actually scan, skipping (and warning about) any whose
        // emulator/profile/platform can't be resolved or whose RomType has no scanner. Snapshotting to a
        // list also detaches us from concurrent edits to the Settings.Mappings collection during the scan.
        private List<KeyValuePair<EmulatorMapping, RomTypeScanner>> BuildScanJobs()
        {
            var jobs = new List<KeyValuePair<EmulatorMapping, RomTypeScanner>>();
            foreach (var mapping in Settings.Mappings?.Where(m => m.Enabled) ?? Enumerable.Empty<EmulatorMapping>())
            {
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

                jobs.Add(new KeyValuePair<EmulatorMapping, RomTypeScanner>(mapping, scanner));
            }
            return jobs;
        }

        // Scans the mappings, fanning them out across at most MaxScanConcurrency worker threads. A single
        // mapping is scanned inline to avoid threading overhead. Per-mapping work is independent (the shared
        // per-file ScanCache is internally synchronized), so the only ordering change is harmless
        // interleaving — Playnite reconciles emitted games by GameId.
        //
        // Per-device I/O load is bounded by the ScanConcurrency governor (per-host + global permits), which
        // every scanner's I/O passes through, so MaxScanConcurrency here is purely producer fan-out: multiple
        // mappings on *distinct* endpoints progress in parallel, while mappings sharing a host collapse onto
        // that host's budget regardless of how many produce at once.
        private IEnumerable<GameMetadata> ScanMappings(List<KeyValuePair<EmulatorMapping, RomTypeScanner>> jobs, LibraryGetGamesArgs args)
        {
            if (jobs.Count == 0)
                yield break;

            var ct = args.CancelToken;
            var degree = Math.Min(MaxScanConcurrency, jobs.Count);

            if (degree <= 1)
            {
                foreach (var job in jobs)
                {
                    if (ct.IsCancellationRequested)
                        yield break;

                    foreach (var g in ScanOneMapping(job.Key, job.Value, args))
                        yield return g;
                }
                yield break;
            }

            using (var results = new BlockingCollection<GameMetadata>(ScanQueueCapacity))
            {
                var producer = Task.Run(() =>
                {
                    try
                    {
                        var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct };
                        Parallel.ForEach(jobs, options, job =>
                        {
                            foreach (var g in ScanOneMapping(job.Key, job.Value, args))
                                results.Add(g, ct);
                        });
                    }
                    finally
                    {
                        results.CompleteAdding();
                    }
                });

                foreach (var g in results.GetConsumingEnumerable())
                    yield return g;

                // Surface any non-cancellation faults from the producer; cancellation is expected.
                try
                {
                    producer.Wait();
                }
                catch (AggregateException ex)
                {
                    var faults = ex.Flatten().InnerExceptions.Where(e => !(e is OperationCanceledException)).ToList();
                    if (faults.Count > 0)
                        Logger.Error(new AggregateException(faults), "One or more mapping scans failed.");
                }
            }
        }

        // Wraps a single mapping's scan with timing and exception isolation: a failure mid-enumeration is
        // logged and ends that mapping only, so it can't abort the other (possibly concurrent) scans.
        private IEnumerable<GameMetadata> ScanOneMapping(EmulatorMapping mapping, RomTypeScanner scanner, LibraryGetGamesArgs args)
        {
            var stopwatch = Stopwatch.StartNew();
            var count = 0;

            using (var enumerator = scanner.GetGames(mapping, args).GetEnumerator())
            {
                while (true)
                {
                    GameMetadata game;
                    try
                    {
                        if (!enumerator.MoveNext())
                            break;
                        game = enumerator.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"Error scanning mapping {mapping.MappingId} ({mapping.RomType}, {mapping.SourcePath}). Skipping remainder of this mapping.");
                        break;
                    }

                    count++;
                    yield return game;
                }
            }

            stopwatch.Stop();
            Logger.Info($"Scanned mapping {mapping.MappingId} ({mapping.RomType}) — {count} game(s) in {stopwatch.Elapsed.TotalSeconds:F1}s.");
        }

        public override ISettings GetSettings(bool firstRunSettings) => Settings;
        public override UserControl GetSettingsView(bool firstRunSettings) => new SettingsView();

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
                Action = (arags) => RemoveSuperUninstalledGames(true, default),
                Description = "Remove uninstalled games with missing source file...",
                MenuSection = "EmuLibrary"
            };
            yield return new MainMenuItem()
            {
                Action = (arags) => ClearScanCache(),
                Description = "Clear scan cache",
                MenuSection = "EmuLibrary"
            };
        }

        private void ClearScanCache()
        {
            try
            {
                _scanCache.Clear();
                Playnite.Dialogs.ShowMessage("Scan cache cleared. It will be rebuilt on the next library update.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to clear scan cache.");
                Playnite.Dialogs.ShowErrorMessage($"Failed to clear scan cache: {ex.Message}", "EmuLibrary");
            }
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var ourGameInfos = args.Games.Select(game =>
            {
                if (game.PluginId != Id)
                    return (null, null);

                ELGameInfo gameInfo;
                try
                {
                    gameInfo = game.GetELGameInfo();
                }
                catch
                {
                    return (null, null);
                }

                return (game, gameInfo);
            }).Where(ggi => ggi.game != null);

            if (ourGameInfos.Any())
            {
                yield return new GameMenuItem()
                {
                    Action = (arags) =>
                    {
                        ourGameInfos.ForEach(ggi => ggi.gameInfo.BrowseToSource());
                    },
                    Description = "Browse to Source...",
                    MenuSection = "EmuLibrary"
                };
                yield return new GameMenuItem()
                {
                    Action = (arags) =>
                    {
                        var text = ourGameInfos.Select(ggi => ggi.gameInfo.ToDescriptiveString(ggi.game))
                            .Aggregate((a, b) => $"{a}\n--------------------------------------------------------------------\n{b}");
                        Playnite.Dialogs.ShowSelectableString("Decoded GameId info for each selected game is shown below. This information can be useful for troubleshooting.", "EmuLibrary Game Info", text);
                    },
                    Description = "Show Debug Info...",
                    MenuSection = "EmuLibrary"
                };
            }
        }

        private void RemoveSuperUninstalledGames(bool promptUser, CancellationToken ct)
        {
            var toRemove = _scanners.Values.SelectMany(s => s.GetUninstalledGamesMissingSourceFiles(ct));
            if (toRemove.Any())
            {
                System.Windows.MessageBoxResult res;
                if (promptUser)
                {
                    res = PlayniteApi.Dialogs.ShowMessage($"Delete {toRemove.Count()} library entries?\n\n(This may take a while, during while Playnite will seem frozen.)", "Confirm deletion", System.Windows.MessageBoxButton.YesNo);
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
            else if (promptUser)
            {
                PlayniteApi.Dialogs.ShowMessage("Nothing to do.");
            }
        }
    }
}