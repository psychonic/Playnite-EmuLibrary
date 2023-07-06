using EmuLibrary.Util;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.MultiFile
{
    class MultiFileInstallController : InstallController
    {
        private readonly IPlayniteAPI _playniteAPI;
        private readonly EmuLibrarySettings _settings;
        private CancellationTokenSource _watcherToken;

        internal MultiFileInstallController(Game game, EmuLibrarySettings settings, IPlayniteAPI playniteAPI) : base(game)
        {
            Name = "Install";
            _playniteAPI = playniteAPI;
            _settings = settings;
        }

        public override void Install(InstallActionArgs args)
        {
            var info = Game.GetMultiFileGameInfo();

            var dstPathBase = info.Mapping?.DestinationPathResolved ??
                throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            _watcherToken = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    var sourceFolder = new DirectoryInfo(info.SourceFullBaseDir);
                    var sourceFile = new FileInfo(Path.Combine(sourceFolder.FullName, info.SourceFilePath));

                    var fc = new FolderCopier()
                    {
                        SourceFolder = sourceFolder,
                        DestinationFolder = new DirectoryInfo(Path.Combine(dstPathBase, sourceFolder.Name))
                    };

                    await fc.CopyAsync(_watcherToken.Token);

                    var installDir = Path.Combine(dstPathBase, info.SourceBaseDir);
                    var gamePath = Path.Combine(new string[] { dstPathBase, info.SourceFilePath });

                    if (_playniteAPI.ApplicationInfo.IsPortable)
                    {
                        installDir = installDir.Replace(_playniteAPI.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
                        gamePath = gamePath.Replace(_playniteAPI.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
                    }

                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
                    {
                        InstallDirectory = installDir,
                        Roms = new List<GameRom>() { new GameRom(Game.Name, gamePath) },
                    }));
                }
                catch (Exception ex)
                {
                    _playniteAPI.Notifications.Add(Game.GameId, $"Failed to install {Game.Name}.{Environment.NewLine}{Environment.NewLine}{ex}", NotificationType.Error);
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
