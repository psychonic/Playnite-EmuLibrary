using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary
{
    class EmuLibraryInstallController : InstallController
    {
        private readonly IPlayniteAPI PlayniteAPI;
        private EmuLibrarySettings Settings;

        internal EmuLibraryInstallController(Game game, EmuLibrarySettings settings, IPlayniteAPI playniteAPI) : base(game)
        {
            Name = "Install";
            Settings = settings;
            PlayniteAPI = playniteAPI;
        }

        public async void AwaitInstall(Game game, string destination)
        {
            var source = new ELPathInfo(Game);

#if !DEBUG
            await Task.Run(() =>
            {
#endif
                source.CopyTo(destination);
#if !DEBUG
        });
#endif

            var installDir = Path.Combine(destination, source.RelativeInstallPath);
            var gamePath = Path.Combine(destination, source.RelativeRomPath);

            if (PlayniteAPI.ApplicationInfo.IsPortable)
            {
                installDir = installDir.Replace(PlayniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory);
                gamePath = gamePath.Replace(PlayniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory);
            }

            InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
            {
                InstallDirectory = installDir,
                Roms = new List<GameRom>() {  new GameRom(game.Name, gamePath)},
            }));

        }

        public override void Install(InstallActionArgs args)
        {
            var playAction = Game.GameActions.Where(ga => ga.IsPlayAction).First();
            var dstPath = Settings.Mappings.First(m => m.EmulatorId == playAction.EmulatorId && m.EmulatorProfileId == playAction.EmulatorProfileId).DestinationPathResolved;
            AwaitInstall(Game, dstPath);
        }
    }
}
