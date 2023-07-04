using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.SingleFile
{
    class SingleFileInstallController : InstallController
    {
        private readonly IPlayniteAPI _playniteAPI;
        private readonly EmuLibrarySettings _settings;
        private CancellationTokenSource _watcherToken;

        internal SingleFileInstallController(Game game, EmuLibrarySettings settings, IPlayniteAPI playniteAPI) : base(game)
        {
            Name = "Install";
            _playniteAPI = playniteAPI;
            _settings = settings;
        }

        public override void Install(InstallActionArgs args)
        {
            var info = Game.GetSingleFileGameInfo();

            var dstPath = (_settings.Mappings.FirstOrDefault(m => m.MappingId == info.MappingId)?.DestinationPathResolved) ??
                throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            _watcherToken = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    using (var src = File.Open(info.SourceFullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        using (var dst = File.Create(Path.Combine(dstPath, info.SourcePath)))
                        {
                            await src.CopyToAsync(dst, 81920 /* default */, _watcherToken.Token);
                        }
                    }

                    var installDir = dstPath;
                    var gamePath = Path.Combine(dstPath, info.SourcePath);

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
