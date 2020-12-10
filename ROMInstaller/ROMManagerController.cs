using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ROMManager
{
    class ROMManagerController : IGameController
    {
        protected readonly SynchronizationContext execContext;
        readonly IPlayniteAPI PlayniteAPI;

        internal ROMManagerController(Game game, ROMInstallerSettings settings, IPlayniteAPI playniteAPI)
        {
            Game = game;
            Settings = settings;
            execContext = SynchronizationContext.Current;
            PlayniteAPI = playniteAPI;
        }
        public bool IsGameRunning
        {
            get; private set;
        }

        public Game Game { get; private set; }
        private ROMInstallerSettings Settings;

        #region Unused events
        public event EventHandler<GameControllerEventArgs> Starting
        {
            add { }
            remove { }
        }
        public event EventHandler<GameControllerEventArgs> Started
        {
            add { }
            remove { }
        }
        public event EventHandler<GameControllerEventArgs> Stopped
        {
            add { }
            remove { }
        }
        #endregion

        public event EventHandler<GameControllerEventArgs> Uninstalled;
        public event EventHandler<GameInstalledEventArgs> Installed;

        public void Dispose()
        {
        }

        public void Install()
        {
            var dstPath = Settings.Mappings.First(m => m.EmulatorId == Game.PlayAction.EmulatorId && m.EmulatorProfileId == Game.PlayAction.EmulatorProfileId).DestinationPath;
            var progressOptions = new GlobalProgressOptions($"Installing {Game.Name}...", false) { IsIndeterminate = true };
            PlayniteAPI.Dialogs.ActivateGlobalProgress((progressAction) =>
            {
                AwaitInstall(Game, dstPath);
            }, progressOptions);
        }

        public async void AwaitInstall(Game game, string destination)
        {
            var source = new RMPathInfo(Game);
            var stopWatch = Stopwatch.StartNew();

            await source.CopyTo(destination);

            var gameInfo = new GameInfo() {
                InstallDirectory = Path.Combine(destination, source.RelativeInstallPath),
                GameImagePath = Path.Combine(destination, source.RelativeRomPath),
            };
            stopWatch.Stop();
            execContext.Post((a) => Installed?.Invoke(this, new GameInstalledEventArgs(gameInfo, this, stopWatch.Elapsed.TotalSeconds)), null);

            // This is actually ignored in the GameInfo above... Maybe fixed now in latest version. TODO: recheck
            var g = PlayniteAPI.Database.Games[Game.Id];
            g.GameImagePath = gameInfo.GameImagePath;
            PlayniteAPI.Database.Games.Update(g);
            //
        }

        public void Play()
        {
        }

        public void Uninstall()
        {
            var info = new FileInfo(Game.GameImagePath);
            if (info.Exists)
            {
                var pathInfo = new RMPathInfo(Game);
                if (pathInfo.IsMultiFile)
                {
                    Directory.Delete(info.Directory.FullName, true);
                }
                else
                {
                    File.Delete(Game.GameImagePath);
                }
                execContext.Post((a) => Uninstalled?.Invoke(this, new GameControllerEventArgs(this, 0)), null);
            }
            else
            {
                throw new ArgumentException($"\"{info.FullName}\" does not exist");
            }
        }
    }
}
