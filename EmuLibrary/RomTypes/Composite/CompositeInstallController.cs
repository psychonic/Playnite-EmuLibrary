using EmuLibrary.Installs;
using EmuLibrary.Util.FileCopier;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes
{
    // The canonical install sequence shared by composite-content RomTypes (base -> updates -> DLC ->
    // licenses), with the per-platform primitives behind abstract hooks. Updates are selected/ordered by
    // CompositeContent.SelectUpdatesToInstall using the platform's Strategy + VersionKey; everything else
    // (how a base/update/DLC/license is actually applied, and where the install/rom paths land) is
    // platform-specific. TTitle is the in-memory composite (ICompositeContentSet<TItem>) re-derived at
    // install time by ResolveComposite; TItem is the platform's content-item type (file path, Ps3FileInfo, ...).
    abstract class CompositeInstallController<TTitle, TItem> : BaseInstallController
        where TTitle : ICompositeContentSet<TItem>
    {
        internal CompositeInstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
        }

        // The active install handle for the current Install() run; drives the CopyProgress reporter and the
        // per-phase byte reporters CreateByteProgress hands out. Null until Install() runs.
        private IInstallProgressHandle _handle;

        // Byte-level copy progress reporter for hooks that copy files (e.g. a disc-image base). Pass it to
        // CreateFileCopier(...).CopyAsync so the copy shows a real percentage bar; forwards to the active
        // install handle. Null until Install() runs.
        protected IProgress<CopyProgress> CopyProgress { get; private set; }

        // A byte-progress reporter for a crypto/extract phase whose total is known up front (e.g. a 3DS
        // content-store import or a PS3 PKG extract). The primitives report INCREMENTAL bytes per chunk; the
        // returned reporter accumulates them and forwards the running total to the install handle as a
        // percentage bar. Returns null before Install() runs (the primitives treat null as "don't report").
        protected IProgress<long> CreateByteProgress(long totalBytes) =>
            _handle == null ? null : new ByteProgressReporter(_handle, totalBytes);

        // Family A (content-store) installs the latest update only; Family B (disc+package) installs all
        // updates in ascending version order.
        protected abstract UpdateInstallStrategy Strategy { get; }

        // Orders updates for install. The version key is the ONLY thing that orders updates, never filename
        // or enumeration order.
        protected abstract Func<TItem, IComparable> VersionKey { get; }

        // Re-derive the title's composite from the source on demand (the source may have changed since the
        // scan), typically via the platform scanner's BuildTitle.
        protected abstract TTitle ResolveComposite(CancellationToken ct);

        protected abstract Task InstallBaseAsync(TTitle title, CancellationToken ct);
        protected abstract Task InstallUpdateAsync(TTitle title, TItem update, CancellationToken ct);
        protected abstract Task InstallDlcAsync(TTitle title, TItem dlc, CancellationToken ct);

        // No-op for the content-store family (Family A), which has no license files.
        protected virtual Task InstallLicenseAsync(TTitle title, TItem license, CancellationToken ct) =>
            Task.CompletedTask;

        // Final install directory and launchable rom path, resolved after the base is installed.
        protected abstract string GetInstallDir(TTitle title);
        protected abstract string GetRomPath(TTitle title);

        // Name reported for the GameRom; defaults to the Playnite game name.
        protected virtual string GetRomName(TTitle title) => Game.Name;

        // Best-effort removal of partial output after a canceled or failed install, so a half-installed title
        // isn't left on disk. Critical for content-store families whose installed-ness is derived from disk on
        // each scan (a leftover partial dir would otherwise resurface as an "installed" but corrupt game).
        // Routed through each platform's own uninstall primitive (uninstall stays per-family, not abstracted);
        // the default is a no-op for copy-based families, which harden mid-copy cleanup in a later phase.
        protected virtual void CleanupPartialInstall()
        {
        }

        private void TryCleanupPartialInstall()
        {
            try
            {
                CleanupPartialInstall();
            }
            catch (Exception cleanupEx)
            {
                _emuLibrary.Logger.Warn(cleanupEx, $"Failed to clean up partial install of \"{Game.Name}\".");
            }
        }

        public override void Install(InstallActionArgs args)
        {
            if (!TryBeginInstall())
                return;

            _watcherToken = new CancellationTokenSource();

            var handle = _emuLibrary.InstallProgress.Begin(Game, _watcherToken);
            _handle = handle;
            CopyProgress = new CopyProgressReporter(handle);

            Task.Run(async () =>
            {
                try
                {
                    var ct = _watcherToken.Token;

                    handle.SetIndeterminate("Preparing…");
                    var title = ResolveComposite(ct);

                    handle.SetStage("Installing base…");
                    await InstallBaseAsync(title, ct);

                    var updates = CompositeContent.SelectUpdatesToInstall(title.Updates, Strategy, VersionKey).ToList();
                    for (int i = 0; i < updates.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        handle.SetStage($"Installing update {i + 1} of {updates.Count}…");
                        await InstallUpdateAsync(title, updates[i], ct);
                    }

                    var dlc = title.Dlc.ToList();
                    for (int i = 0; i < dlc.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        handle.SetStage($"Installing DLC {i + 1} of {dlc.Count}…");
                        await InstallDlcAsync(title, dlc[i], ct);
                    }

                    var licenses = title.Licenses.ToList();
                    for (int i = 0; i < licenses.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        handle.SetStage($"Installing license {i + 1} of {licenses.Count}…");
                        await InstallLicenseAsync(title, licenses[i], ct);
                    }

                    handle.SetIndeterminate("Finishing…");
                    var installDir = MaybeMakePortable(GetInstallDir(title));
                    var romPath = MaybeMakePortable(GetRomPath(title));

                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
                    {
                        InstallDirectory = installDir,
                        Roms = new List<GameRom>() { new GameRom(GetRomName(title), romPath) },
                    }));

                    handle.Complete();
                }
                catch (OperationCanceledException)
                {
                    handle.Canceled();
                    TryCleanupPartialInstall();
                    Game.IsInstalling = false;
                    throw;
                }
                catch (WindowsCopyDialogClosedException)
                {
                    handle.Canceled();
                    TryCleanupPartialInstall();
                    Game.IsInstalling = false;
                    throw;
                }
                catch (Exception ex)
                {
                    handle.Fail(ex);
                    TryCleanupPartialInstall();
                    _emuLibrary.Playnite.Notifications.Add(Game.GameId, $"Failed to install {Game.Name}.{Environment.NewLine}{Environment.NewLine}{ex}", NotificationType.Error);
                    Game.IsInstalling = false;
                    throw;
                }
                finally
                {
                    EndInstall();
                }
            });
        }

        protected string MaybeMakePortable(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (_emuLibrary.Playnite.ApplicationInfo.IsPortable)
                return path.Replace(_emuLibrary.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
            return path;
        }
    }
}
