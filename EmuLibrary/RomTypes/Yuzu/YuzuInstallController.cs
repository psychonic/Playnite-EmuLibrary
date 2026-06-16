using EmuLibrary.Util.FileCopier;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.Yuzu
{
    class YuzuInstallController : BaseInstallController
    {
        private readonly YuzuGameInfo _gameInfo;
        private readonly YuzuScanner _scanner;

        internal YuzuInstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetYuzuGameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Yuzu) as YuzuScanner;
            Name = string.Format("Install to {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        public override void Install(InstallActionArgs args)
        {
            var mapping = _gameInfo.Mapping
                ?? throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            _watcherToken = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    var ct = _watcherToken.Token;
                    var yuzu = new Yuzu(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);

                    // Re-derive the title's composite from the source dir at install time (the source files
                    // may have changed since the scan), exactly like PS3.
                    var title = _scanner.BuildTitle(mapping, _gameInfo.TitleId, ct)
                        ?? throw new Exception($"Source content for \"{Game.Name}\" could not be found under \"{mapping.SourcePath}\".");

                    await Task.Run(() =>
                    {
                        yuzu.InstallFileToNand(title.ProgramFile);
                        if (title.UpdateFile != null)
                        {
                            yuzu.InstallFileToNand(title.UpdateFile);
                        }
                        title.DlcFiles.ForEach(dlc => yuzu.InstallFileToNand(dlc));
                    }, ct);

                    var romPath = MaybeMakePortable(yuzu.GetLaunchPathFromTitleId(_gameInfo.TitleId));
                    var installDir = MaybeMakePortable(mapping.EmulatorBasePathResolved);

                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
                    {
                        InstallDirectory = installDir,
                        Roms = new List<GameRom>() { new GameRom(title.Name, romPath) },
                    }));
                }
                catch (Exception ex)
                {
                    if (!(ex is WindowsCopyDialogClosedException))
                    {
                        _emuLibrary.Playnite.Notifications.Add(Game.GameId, $"Failed to install {Game.Name}.{Environment.NewLine}{Environment.NewLine}{ex}", NotificationType.Error);
                    }
                    Game.IsInstalling = false;
                    throw;
                }
            });
        }

        private string MaybeMakePortable(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (_emuLibrary.Playnite.ApplicationInfo.IsPortable)
                return path.Replace(_emuLibrary.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
            return path;
        }
    }
}
