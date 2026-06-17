using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.IO;
using System.Windows;

namespace EmuLibrary.RomTypes.MultiFile
{
    class MultiFileUninstallController : UninstallController
    {
        private readonly IEmuLibrary _emuLibrary;

        internal MultiFileUninstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            Name = "Uninstall";
            _emuLibrary = emuLibrary;
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var gameInstallDirectoryResolved = Game.InstallDirectory.Replace(ExpandableVariables.PlayniteDirectory, _emuLibrary.Playnite.Paths.ApplicationPath);
            var installDir = new DirectoryInfo(gameInstallDirectoryResolved);
            if (installDir.Exists)
            {
                // For a Symlink install the install dir is a directory symlink to the source - delete only the
                // reparse point (non-recursive), never recurse into it, or we'd wipe the source's contents. A
                // Copy or Hardlink install is a real directory we own, so delete it recursively.
                if (installDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    installDir.Delete(false);
                }
                else
                {
                    Directory.Delete(gameInstallDirectoryResolved, true);
                }
            }
            else
            {
                _emuLibrary.Playnite.Dialogs.ShowMessage($"\"{Game.Name}\" does not appear to be installed. Marking as uninstalled.", "Game not installed", MessageBoxButton.OK);
            }
            Game.Roms.Clear();
            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }
    }
}
