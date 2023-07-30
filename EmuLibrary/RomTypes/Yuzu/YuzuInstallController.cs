using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.Yuzu
{
    class YuzuInstallController : InstallController
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly SourceDirCache _cache;
        private readonly YuzuGameInfo _gameInfo;

        private CancellationTokenSource _watcherToken;

        internal YuzuInstallController(Game game, IEmuLibrary emuLibrary) : base(game)
        {
            Name = "Install to Yuzu";
            _emuLibrary = emuLibrary;

            _gameInfo = game.GetYuzuGameInfo();
            _cache = (_emuLibrary.GetScanner(RomType.Yuzu) as YuzuScanner).GetCacheForMapping(_gameInfo.MappingId);

            Name = string.Format("Install to {0}", _gameInfo.Mapping.Emulator?.Name ?? "Emulator");
        }
        public bool IsGameRunning
        {
            get; private set;
        }

        public override void Install(InstallActionArgs args)
        {
            _watcherToken = new CancellationTokenSource();

            Task.Run(async () =>
            {
                var yuzu = new Yuzu(_gameInfo.Mapping.EmulatorBasePathResolved, _emuLibrary.Logger);

                if (_cache.IsDirty)
                {
                    await Task.Run(() =>
                    {
                        _cache.Refresh(_watcherToken.Token);
                    });
                }

                // get dirs from cache, and do for each
                var gameCache = _cache.TheCache.UninstalledGames[_gameInfo.TitleId];

                await Task.Run(() =>
                {
                    yuzu.InstallFileToNand(gameCache.ProgramFile);
                    if (gameCache.UpdateFile != null)
                    {
                        yuzu.InstallFileToNand(gameCache.UpdateFile);
                    }
                    gameCache.DlcFiles.ForEach(dlc => yuzu.InstallFileToNand(dlc));
                });

                var subPath = yuzu.GetLaunchPathFromTitleId(_gameInfo.TitleId);
                var gamePath = subPath;
                var installDir = _gameInfo.Mapping.EmulatorBasePathResolved;

                if (_emuLibrary.Playnite.ApplicationInfo.IsPortable)
                {
                    // Already "unresolved" path, has {PlayniteDir} already potentially in it
                    installDir = _gameInfo.Mapping.EmulatorBasePath;

                    // Raw disk path, needs to be "unresolved" to insert {PlayniteDir} if necessary
                    gamePath = gamePath.Replace(_emuLibrary.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
                }

                _cache.TheCache.InstalledGames[gameCache.TitleId] = new SourceDirCache.CacheGameInstalled()
                {
                    TitleId = gameCache.TitleId,
                    Title = gameCache.Title,
                    Publisher = gameCache.Publisher,
                    Version = gameCache.Version,
                    ProgramNcaSubPath = subPath,
                };

                var installData = new GameInstallationData()
                {
                    InstallDirectory = installDir,
                    Roms = new List<GameRom>() {
                        new GameRom() {
                            Name = gameCache.Title,
                            Path = gamePath
                        }
                    },
                };

                InvokeOnInstalled(new GameInstalledEventArgs(installData));
            });
        }

        public override void Dispose()
        {
            _watcherToken?.Cancel();
            base.Dispose();
        }
    }
}
