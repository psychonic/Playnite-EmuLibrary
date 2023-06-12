using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Linq;

namespace EmuLibrary.RomTypes.SingleFile
{
    class SingleFileUninstallController : UninstallController
    {
        private readonly IPlayniteAPI PlayniteAPI;

        internal SingleFileUninstallController(Game game, IPlayniteAPI playniteAPI) : base(game)
        {
            Name = "Uninstall";
            PlayniteAPI = playniteAPI;
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var gameImagePathResolved = Game.Roms.First().Path.Replace(ExpandableVariables.PlayniteDirectory, PlayniteAPI.Paths.ApplicationPath);
            var info = new FileInfo(gameImagePathResolved);
            if (info.Exists)
            {
                File.Delete(gameImagePathResolved);
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            }
            else
            {
                throw new ArgumentException($"\"{info.FullName}\" does not exist");
            }
        }
    }
}
