using EmuLibrary.Util.FileCopier;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.IO;
using System.Threading;

namespace EmuLibrary.RomTypes
{
    abstract class BaseInstallController : InstallController
    {
        protected readonly IEmuLibrary _emuLibrary;
        protected CancellationTokenSource _watcherToken;

        internal BaseInstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            Name = "Install";
            _emuLibrary = emuLibrary;
        }

        public override void Dispose()
        {
            _watcherToken?.Cancel();
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
