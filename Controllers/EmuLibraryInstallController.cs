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
        private CancellationTokenSource WatcherToken;

        internal EmuLibraryInstallController(Game game, EmuLibrarySettings settings, IPlayniteAPI playniteAPI) : base(game)
        {
            Name = "Install";
            Settings = settings;
            PlayniteAPI = playniteAPI;
        }

        public override void Install(InstallActionArgs args)
        {
            var playAction = Game.GameActions.Where(ga => ga.IsPlayAction).First();
            var dstPath = Settings.Mappings.FirstOrDefault(m => m.EmulatorId == playAction.EmulatorId && m.EmulatorProfileId == playAction.EmulatorProfileId)?.DestinationPathResolved;
            if (dstPath == null)
            {
                throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");
            }

            WatcherToken = new CancellationTokenSource();

            Task.Run(async () =>
            {
                var source = new ELPathInfo(Game);

                await source.CopyTo(dstPath, WatcherToken.Token);

                var installDir = Path.Combine(dstPath, source.RelativeInstallPath);
                var gamePath = Path.Combine(dstPath, source.RelativeRomPath);

                if (PlayniteAPI.ApplicationInfo.IsPortable)
                {
                    installDir = installDir.Replace(PlayniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory);
                    gamePath = gamePath.Replace(PlayniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory);
                }

                InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
                {
                    InstallDirectory = installDir,
                    Roms = new List<GameRom>() { new GameRom(Game.Name, gamePath) },
                }));
            });
        }

        public override void Dispose()
        {
            WatcherToken?.Cancel();
            base.Dispose();
        }
    }
}
