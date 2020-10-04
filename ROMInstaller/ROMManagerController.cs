using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Diagnostics;

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

        public event EventHandler<GameControllerEventArgs> Starting;
        public event EventHandler<GameControllerEventArgs> Started;
        public event EventHandler<GameControllerEventArgs> Stopped;
        public event EventHandler<GameControllerEventArgs> Uninstalled;
        public event EventHandler<GameInstalledEventArgs> Installed;

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public void Install()
        {
            //throw new NotImplementedException();
            var srcInfo = new FileInfo(Game.GameId);
            var dstPath = Settings.Mappings.First(m => m.EmulatorId == Game.PlayAction.EmulatorId && m.EmulatorProfileId == Game.PlayAction.EmulatorProfileId).DestinationPath;
            AwaitInstall(srcInfo.FullName, Path.Combine(dstPath, srcInfo.Name));
        }

        public async void AwaitInstall(string source, string destination)
        {
            var stopWatch = Stopwatch.StartNew();
            //File.Copy(source, destination);
            using (FileStream SourceStream = File.Open(source, FileMode.Open))
            {
                using (FileStream DestinationStream = File.Create(destination))
                {
                    await SourceStream.CopyToAsync(DestinationStream);
                }
            }
            var gameInfo = new GameInfo() {
                InstallDirectory = (new FileInfo(destination)).Directory.FullName,
                GameImagePath = destination
            };
            stopWatch.Stop();
            execContext.Post((a) => Installed?.Invoke(this, new GameInstalledEventArgs(gameInfo, this, stopWatch.Elapsed.TotalSeconds)), null);

            // This is actually ignored in the GameInfo above...
            var game = PlayniteAPI.Database.Games[Game.Id];
            game.GameImagePath = destination;
            PlayniteAPI.Database.Games.Update(game);
            //
        }

        public void Play()
        {
            //throw new NotImplementedException();
        }

        public void Uninstall()
        {
            if (File.Exists(Game.GameImagePath))
            {
                File.Delete(Game.GameImagePath);
                execContext.Post((a) => Uninstalled?.Invoke(this, new GameControllerEventArgs(this, 0)), null);
            }
            else
            {
                throw new ArgumentException();
            }
        }
    }
}
