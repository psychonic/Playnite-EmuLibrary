using EmuLibrary.Util.FileCopier;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace EmuLibrary.RomTypes
{
    abstract class BaseInstallController : InstallController
    {
        // Game ids whose install is currently in flight, shared across all controller instances. Playnite
        // creates a fresh controller per install request, so a second request (e.g. double-clicking a game
        // that is already installing) would otherwise start a duplicate copy and surface an error. Each
        // Install begins with TryBeginInstall() and releases via EndInstall() when it finishes.
        private static readonly HashSet<string> _installingGameIds = new HashSet<string>();
        private bool _holdsInstallLock;

        protected readonly IEmuLibrary _emuLibrary;
        protected CancellationTokenSource _watcherToken;

        internal BaseInstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            Name = "Install";
            _emuLibrary = emuLibrary;
        }

        // Returns false if an install for this game is already running (in which case the caller should do
        // nothing and return), otherwise marks it in-flight. Pair every true result with EndInstall().
        protected bool TryBeginInstall()
        {
            lock (_installingGameIds)
            {
                if (!_installingGameIds.Add(Game.GameId))
                {
                    _emuLibrary.Logger.Info($"Ignoring duplicate install request for \"{Game.Name}\" — an install is already in progress.");
                    return false;
                }
                _holdsInstallLock = true;
                return true;
            }
        }

        protected void EndInstall()
        {
            if (!_holdsInstallLock)
                return;
            lock (_installingGameIds)
            {
                _installingGameIds.Remove(Game.GameId);
                _holdsInstallLock = false;
            }
        }

        public override void Dispose()
        {
            _watcherToken?.Cancel();
            EndInstall();
            base.Dispose();
        }

        protected bool UseWindowsCopyDialog()
        {
            if (_emuLibrary.Playnite.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                return _emuLibrary.Settings.UseWindowsCopyDialogInDesktopMode;
            }
            else if (_emuLibrary.Playnite.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
            {
                return _emuLibrary.Settings.UseWindowsCopyDialogInFullscreenMode;
            }
            return false;
        }

        protected IFileCopier CreateFileCopier(FileSystemInfo source, DirectoryInfo destination, InstallMethod method = InstallMethod.Copy)
        {
            switch (method)
            {
                case InstallMethod.Symlink:
                    return new SymlinkFileCopier(source, destination);
                case InstallMethod.Hardlink:
                    return new HardlinkFileCopier(source, destination);
                default:
                    // The Windows copy dialog only makes sense for an actual byte copy; linking is instant.
                    if (UseWindowsCopyDialog())
                    {
                        return new WindowsFileCopier(source, destination);
                    }
                    return new SimpleFileCopier(source, destination);
            }
        }
    }
}
