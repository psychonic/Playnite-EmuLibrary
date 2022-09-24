using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.IO;
using System.Linq;

namespace EmuLibrary
{
    class EmuLibraryUninstallController : UninstallController
    {
        private readonly IPlayniteAPI PlayniteAPI;

        internal EmuLibraryUninstallController(Game game, IPlayniteAPI playniteAPI) : base(game)
        {
            Name = "Uninstall";
            PlayniteAPI = playniteAPI;
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var gameImagePathResolved = Game.Roms.First().Path.Replace(Playnite.SDK.ExpandableVariables.PlayniteDirectory, PlayniteAPI.Paths.ApplicationPath);
            var info = new FileInfo(gameImagePathResolved);
            if (info.Exists)
            {
                var pathInfo = new ELPathInfo(Game);
                if (pathInfo.IsMultiFile)
                {
                    Directory.Delete(Game.InstallDirectory.Replace(Playnite.SDK.ExpandableVariables.PlayniteDirectory, PlayniteAPI.Paths.ApplicationPath), true);
                }
                else
                {
                    File.Delete(gameImagePathResolved);
                }
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            }
            else
            {
                throw new ArgumentException($"\"{info.FullName}\" does not exist");
            }
        }
    }
}
