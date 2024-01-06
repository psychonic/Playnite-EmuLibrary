using EmuLibrary.Util;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.SingleFile
{
    class SingleFileInstallController : InstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private CancellationTokenSource _watcherToken;

        internal SingleFileInstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            Name = "Install";
            _emuLibrary = emuLibrary;
        }

        public override void Install(InstallActionArgs args)
        {
            var info = Game.GetSingleFileGameInfo();

            var dstPath = info.Mapping?.DestinationPathResolved ??
                throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            var fc = new FileCopier()
            {
                SourceFile = new FileInfo(info.SourceFullPath),
                DestinationFolder = new DirectoryInfo(dstPath)
            };

            _watcherToken = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    await fc.CopyAsync(_watcherToken.Token);

                    var installDir = dstPath;
                    var gamePath = Path.Combine(dstPath, info.SourcePath);

                    if (_emuLibrary.Playnite.ApplicationInfo.IsPortable)
                    {
                        installDir = installDir.Replace(_emuLibrary.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
                        gamePath = gamePath.Replace(_emuLibrary.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
                    }

                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
                    {
                        InstallDirectory = installDir,
                        Roms = new List<GameRom>() { new GameRom(Game.Name, gamePath) },
                    }));
                }
                catch (Exception ex)
                {
                    if (!(ex is CopyDialogClosedException))
                    {
                        _emuLibrary.Playnite.Notifications.Add(Game.GameId, $"Failed to install {Game.Name}.{Environment.NewLine}{Environment.NewLine}{ex}", NotificationType.Error);
                    }
                    Game.IsInstalling = false;
                    throw;
                }
            });
        }

        public override void Dispose()
        {
            _watcherToken?.Cancel();
            base.Dispose();
        }
    }
}
