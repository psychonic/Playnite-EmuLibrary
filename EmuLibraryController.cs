using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary
{
    class EmuLibraryController : IGameController
    {
        protected readonly SynchronizationContext execContext;
        readonly IPlayniteAPI PlayniteAPI;

        internal EmuLibraryController(Game game, EmuLibrarySettings settings, IPlayniteAPI playniteAPI)
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
        private EmuLibrarySettings Settings;

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
            var dstPath = Settings.Mappings.First(m => m.EmulatorId == Game.PlayAction.EmulatorId && m.EmulatorProfileId == Game.PlayAction.EmulatorProfileId && m.PlatformId == Game.PlatformId).DestinationPathResolved;
            AwaitInstall(Game, dstPath);
        }

        public
#if !DEBUG
            async
#endif
            void AwaitInstall(Game game, string destination)
        {
            var source = new ELPathInfo(Game);
            var stopWatch = Stopwatch.StartNew();

#if !DEBUG
            await
#endif
            source.CopyTo(destination);

            var installDir = Path.Combine(destination, source.RelativeInstallPath);
            var gamePath = Path.Combine(destination, source.RelativeRomPath);

            if (PlayniteAPI.ApplicationInfo.IsPortable)
            {
                installDir = installDir.Replace(PlayniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory);
                gamePath = gamePath.Replace(PlayniteAPI.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory);
            }

            var gameInfo = new GameInfo() {
                InstallDirectory = installDir,
                GameImagePath = gamePath,
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
            var gameImagePathResolved = Game.GameImagePath.Replace(Playnite.SDK.ExpandableVariables.PlayniteDirectory, PlayniteAPI.Paths.ApplicationPath);
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
                execContext.Post((a) => Uninstalled?.Invoke(this, new GameControllerEventArgs(this, 0)), null);
            }
            else
            {
                throw new ArgumentException($"\"{info.FullName}\" does not exist");
            }
        }
    }
}
