using EmuLibrary.Util.FileCopier;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
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

        public override void Install(InstallActionArgs args)
        {
            if (!TryBeginInstall())
                return;

            _watcherToken = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    var ct = _watcherToken.Token;

                    var title = ResolveComposite(ct);

                    await InstallBaseAsync(title, ct);

                    foreach (var update in CompositeContent.SelectUpdatesToInstall(title.Updates, Strategy, VersionKey))
                    {
                        ct.ThrowIfCancellationRequested();
                        await InstallUpdateAsync(title, update, ct);
                    }

                    foreach (var dlc in title.Dlc)
                    {
                        ct.ThrowIfCancellationRequested();
                        await InstallDlcAsync(title, dlc, ct);
                    }

                    foreach (var license in title.Licenses)
                    {
                        ct.ThrowIfCancellationRequested();
                        await InstallLicenseAsync(title, license, ct);
                    }

                    var installDir = MaybeMakePortable(GetInstallDir(title));
                    var romPath = MaybeMakePortable(GetRomPath(title));

                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
                    {
                        InstallDirectory = installDir,
                        Roms = new List<GameRom>() { new GameRom(GetRomName(title), romPath) },
                    }));
                }
                catch (Exception ex)
                {
                    if (!(ex is WindowsCopyDialogClosedException))
                    {
                        _emuLibrary.Playnite.Notifications.Add(Game.GameId, $"Failed to install {Game.Name}.{Environment.NewLine}{Environment.NewLine}{ex}", NotificationType.Error);
                    }
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
