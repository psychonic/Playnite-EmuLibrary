using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace EmuLibrary.Installs
{
    // Singleton progress read model. Install controllers call Begin() to register an in-flight install and
    // report through the returned handle; the Installs panel binds ViewModel. All collection/item mutation
    // is marshalled onto the UI thread via IUiDispatcher (centralized here, never in controllers).
    internal sealed class InstallProgressService : IInstallProgressService
    {
        private readonly IUiDispatcher _dispatcher;
        private readonly Func<Game, string> _iconResolver;
        private readonly ILogger _logger;
        private readonly Action<Guid> _startGame;
        private readonly Action<Guid> _navigateToGame;

        // Keyed by GameId. Registered in Begin, cleared when the row leaves the active set.
        private readonly Dictionary<Guid, Registration> _active = new Dictionary<Guid, Registration>();
        private readonly object _lock = new object();

        // Periodic auto-clear sweep (prod only). Held so it isn't GC'd; null in the test seam.
        private readonly System.Threading.Timer _autoClearTimer;
        private static readonly TimeSpan AutoClearSweepInterval = TimeSpan.FromSeconds(60);

        public InstallsViewModel ViewModel { get; } = new InstallsViewModel();

        // Production wiring: marshal via Playnite's UI dispatcher, resolve icons to full paths via the DB, and
        // launch/navigate through the Playnite API.
        public InstallProgressService(IEmuLibrary emuLibrary)
            : this(new PlayniteUiDispatcher(emuLibrary.Playnite),
                   game => ResolveIconPath(emuLibrary, game),
                   emuLibrary.Logger,
                   id => emuLibrary.Playnite.StartGame(id),
                   id =>
                   {
                       emuLibrary.Playnite.MainView.SwitchToLibraryView();
                       emuLibrary.Playnite.MainView.SelectGame(id);
                   })
        {
            // Sweep finished rows older than the user-configured age (read live from settings each tick, so a
            // toggle takes effect without a restart). The seam ctor below skips this — tests drive SweepFinished.
            _autoClearTimer = new System.Threading.Timer(
                _ => SweepFinished(
                    DateTime.Now,
                    Settings.Settings.Instance?.AutoClearFinishedInstalls ?? false,
                    Settings.Settings.Instance?.AutoClearFinishedInstallsMinutes ?? 0),
                null,
                AutoClearSweepInterval,
                AutoClearSweepInterval);
        }

        // Test/seam wiring: inject a synchronous dispatcher and a trivial icon resolver. The launch/navigate
        // actions are optional (tests that don't exercise them pass null).
        internal InstallProgressService(IUiDispatcher dispatcher, Func<Game, string> iconResolver, ILogger logger,
            Action<Guid> startGame = null, Action<Guid> navigateToGame = null)
        {
            _dispatcher = dispatcher;
            _iconResolver = iconResolver ?? (_ => null);
            _logger = logger;
            _startGame = startGame;
            _navigateToGame = navigateToGame;
        }

        public IInstallProgressHandle Begin(Game game, System.Threading.CancellationTokenSource cts)
        {
            var item = new InstallProgressItem(game.Id, game.Name, _iconResolver(game));
            item.Status = InstallStatus.Installing;
            item.StatusText = "Preparing…";

            var handle = new Handle(this, item);
            var registration = new Registration { Item = item, Cts = cts, Handle = handle };

            _dispatcher.Invoke(() =>
            {
                lock (_lock)
                {
                    // A stale finished row for the same game (re-install) shouldn't linger — drop it first.
                    RemoveFinishedNoLock(game.Id);
                    _active[game.Id] = registration;
                }
                ViewModel.Active.Add(item);
                ViewModel.RaiseCountsChanged();
            });

            return handle;
        }

        public void Cancel(Guid gameId)
        {
            Registration reg;
            lock (_lock)
            {
                _active.TryGetValue(gameId, out reg);
            }

            if (reg == null)
                return;

            reg.Item.StatusText = "Canceling…";
            try
            {
                reg.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The install already finished and disposed its token; nothing to cancel.
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to cancel install for {gameId}.");
            }
        }

        public void ClearFinished(Guid gameId)
        {
            _dispatcher.Invoke(() =>
            {
                RemoveFinishedNoLock(gameId);
                ViewModel.RaiseCountsChanged();
            });
        }

        public void ClearAllFinished()
        {
            _dispatcher.Invoke(() =>
            {
                ViewModel.Finished.Clear();
                ViewModel.Failed.Clear();
                ViewModel.RaiseCountsChanged();
            });
        }

        public void PlayGame(Guid gameId) => _startGame?.Invoke(gameId);

        public void NavigateToGame(Guid gameId) => _navigateToGame?.Invoke(gameId);

        // Removes finished rows whose FinishedOn is older than `minutes`, when enabled. Active installs are
        // never touched. No-op when disabled or minutes <= 0. Internal so the auto-clear timer and tests can
        // both drive it (tests pass a fixed `now`).
        internal void SweepFinished(DateTime now, bool enabled, int minutes)
        {
            if (!enabled || minutes <= 0)
                return;

            var cutoff = now - TimeSpan.FromMinutes(minutes);
            _dispatcher.Invoke(() =>
            {
                var removed = SweepCollection(ViewModel.Finished, cutoff);
                removed |= SweepCollection(ViewModel.Failed, cutoff);
                if (removed)
                    ViewModel.RaiseCountsChanged();
            });
        }

        // Moves a row from the active section to the finished section with a terminal status. Runs on the
        // UI thread. Called by the handle's terminal transitions.
        private void Finish(InstallProgressItem item, InstallStatus status, string statusText)
        {
            _dispatcher.Invoke(() =>
            {
                lock (_lock)
                {
                    _active.Remove(item.GameId);
                }

                item.Status = status;
                item.StatusText = statusText;
                item.IsIndeterminate = false;
                item.FinishedOn = DateTime.Now;
                if (status == InstallStatus.Completed)
                {
                    item.ProgressValue = item.ProgressMaximum;
                }

                // Failed installs go to their own section; completed/canceled go to Finished.
                var target = status == InstallStatus.Failed ? ViewModel.Failed : ViewModel.Finished;
                ViewModel.Active.Remove(item);
                if (!target.Contains(item))
                {
                    target.Insert(0, item);
                }
                ViewModel.RaiseCountsChanged();
            });
        }

        // Removes rows in one collection whose FinishedOn is at/older than cutoff. Returns true if any went.
        private static bool SweepCollection(System.Collections.ObjectModel.ObservableCollection<InstallProgressItem> rows, DateTime cutoff)
        {
            var expired = rows.Where(i => i.FinishedOn.HasValue && i.FinishedOn.Value <= cutoff).ToList();
            foreach (var item in expired)
                rows.Remove(item);
            return expired.Count > 0;
        }

        private void RemoveFinishedNoLock(Guid gameId)
        {
            var finished = ViewModel.Finished.FirstOrDefault(i => i.GameId == gameId);
            if (finished != null)
                ViewModel.Finished.Remove(finished);

            var failed = ViewModel.Failed.FirstOrDefault(i => i.GameId == gameId);
            if (failed != null)
                ViewModel.Failed.Remove(failed);
        }

        private static string ResolveIconPath(IEmuLibrary emuLibrary, Game game)
        {
            try
            {
                if (string.IsNullOrEmpty(game.Icon))
                    return null;
                return emuLibrary.Playnite.Database.GetFullFilePath(game.Icon);
            }
            catch (Exception ex)
            {
                emuLibrary.Logger?.Warn($"Failed to resolve icon path for \"{game.Name}\": {ex.Message}");
                return null;
            }
        }

        private sealed class Registration
        {
            public InstallProgressItem Item;
            public System.Threading.CancellationTokenSource Cts;
            public Handle Handle;
        }

        // Per-install reporter. Marshals updates onto the UI thread and throttles quantitative updates by
        // bytes AND time so a 1 MiB copy/decrypt loop can't flood the dispatcher. Stage/terminal updates are
        // always immediate.
        private sealed class Handle : IInstallProgressHandle
        {
            private const long ThrottleBytes = 512 * 1024;
            private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(250);

            private readonly InstallProgressService _service;
            private readonly InstallProgressItem _item;
            private readonly object _throttleLock = new object();
            private readonly Stopwatch _sinceLastPush = Stopwatch.StartNew();
            private readonly ThroughputEstimator _throughput = new ThroughputEstimator();
            private long _lastPushedDone = -1;
            private bool _finished;
            // Current phase label, used as the prefix for the byte-progress line so a decrypt phase reads
            // "Installing base 42% · …" rather than a hardcoded "Copying…".
            private string _stageLabel = "Installing";

            public Handle(InstallProgressService service, InstallProgressItem item)
            {
                _service = service;
                _item = item;
            }

            public void SetStage(string text) => SetIndeterminate(text);

            public void SetIndeterminate(string text)
            {
                _stageLabel = ProgressFormat.StageLabel(text);
                _service._dispatcher.Invoke(() =>
                {
                    _item.IsIndeterminate = true;
                    _item.StatusText = text;
                });
                ResetThrottle();
            }

            public void SetBytes(long done, long total)
            {
                if (total <= 0)
                {
                    return;
                }

                if (!ShouldPush(done, total))
                    return;

                var rate = _throughput.Sample(done);
                var statusText = ProgressFormat.Line(_stageLabel, done, total, rate);
                _service._dispatcher.Invoke(() =>
                {
                    _item.IsIndeterminate = false;
                    _item.ProgressMaximum = total;
                    _item.ProgressValue = done;
                    _item.StatusText = statusText;
                });
            }

            public void SetPercent(double pct)
            {
                var clamped = pct < 0 ? 0 : (pct > 100 ? 100 : pct);
                if (!ShouldPush((long)clamped, 100))
                    return;

                _service._dispatcher.Invoke(() =>
                {
                    _item.IsIndeterminate = false;
                    _item.ProgressMaximum = 100;
                    _item.ProgressValue = clamped;
                    _item.StatusText = $"{clamped:0}%";
                });
            }

            public void Complete()
            {
                if (!MarkFinishedOnce())
                    return;
                _service.Finish(_item, InstallStatus.Completed, "Completed");
            }

            public void Fail(Exception ex)
            {
                if (!MarkFinishedOnce())
                    return;
                var msg = ex?.Message;
                _service.Finish(_item, InstallStatus.Failed, string.IsNullOrEmpty(msg) ? "Failed" : $"Failed: {msg}");
            }

            public void Canceled()
            {
                if (!MarkFinishedOnce())
                    return;
                _service.Finish(_item, InstallStatus.Canceled, "Canceled");
            }

            private bool MarkFinishedOnce()
            {
                lock (_throttleLock)
                {
                    if (_finished)
                        return false;
                    _finished = true;
                    return true;
                }
            }

            // True if this byte/percent update should reach the UI: forced through on completion of the
            // measured span, otherwise only when both a byte delta and a time floor are exceeded.
            private bool ShouldPush(long done, long total)
            {
                lock (_throttleLock)
                {
                    if (_finished)
                        return false;

                    var isFinalTick = done >= total;
                    var enoughBytes = done - _lastPushedDone >= ThrottleBytes;
                    var enoughTime = _sinceLastPush.Elapsed >= ThrottleInterval;

                    if (isFinalTick || (enoughBytes && enoughTime) || _lastPushedDone < 0)
                    {
                        _lastPushedDone = done;
                        _sinceLastPush.Restart();
                        return true;
                    }
                    return false;
                }
            }

            private void ResetThrottle()
            {
                lock (_throttleLock)
                {
                    _lastPushedDone = -1;
                    _sinceLastPush.Restart();
                }
                // A new phase measures its own throughput from scratch.
                _throughput.Reset();
            }
        }

        // Wraps Playnite's UI dispatcher. Falls back to running inline if the dispatcher is unavailable
        // (e.g. very early startup or a headless/test host), which keeps the service safe to run without UI.
        private sealed class PlayniteUiDispatcher : IUiDispatcher
        {
            private readonly System.Windows.Threading.Dispatcher _dispatcher;

            public PlayniteUiDispatcher(IPlayniteAPI playnite)
            {
                _dispatcher = playnite?.MainView?.UIDispatcher;
            }

            public void Invoke(Action action)
            {
                if (_dispatcher == null || _dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    _dispatcher.Invoke(action);
                }
            }
        }
    }
}
