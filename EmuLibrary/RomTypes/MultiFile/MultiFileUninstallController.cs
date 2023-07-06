using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Linq;

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
            var gameImagePathResolved = Game.Roms.First().Path.Replace(ExpandableVariables.PlayniteDirectory, _emuLibrary.Playnite.Paths.ApplicationPath);
            var info = new FileInfo(gameImagePathResolved);
            if (info.Exists)
            {
                Directory.Delete(Game.InstallDirectory.Replace(ExpandableVariables.PlayniteDirectory, _emuLibrary.Playnite.Paths.ApplicationPath), true);
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            }
            else
            {
                throw new ArgumentException($"\"{info.FullName}\" does not exist");
            }
        }
    }
}
