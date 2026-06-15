using EmuLibrary.Util.FileCopier;
using EmuLibrary.Util.Ps3;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.Ps3
{
    class Ps3InstallController : BaseInstallController
    {
        private readonly Ps3GameInfo _gameInfo;
        private readonly Ps3Scanner _scanner;

        internal Ps3InstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetPs3GameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Ps3) as Ps3Scanner;
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

                    Ps3Scanner.Ps3Title title;
                    if (!string.IsNullOrEmpty(_gameInfo.SourceIsoFileName))
                    {
                        var isoPath = Path.Combine(mapping.SourcePath, _gameInfo.SourceIsoFileName);
                        title = _scanner.BuildLooseIsoTitle(isoPath, ct)
                            ?? throw new Exception($"Source ISO \"{_gameInfo.SourceIsoFileName}\" could not be found at \"{mapping.SourcePath}\".");
                    }
                    else
                    {
                        title = _scanner.BuildTitle(mapping, _gameInfo.SourceFullDir, ct)
                            ?? throw new Exception($"Source content for \"{Game.Name}\" could not be found under \"{_gameInfo.SourceFullDir}\".");
                    }

                    string romPath;
                    string installDir;

                    if (title.BaseKind == Ps3BaseKind.Disc)
                    {
                        var dstPath = mapping.DestinationPathResolved
                            ?? throw new Exception("Destination path cannot be resolved. Please try removing and re-adding the mapping.");
                        Directory.CreateDirectory(dstPath);

                        if (title.DiscIsoPath != null)
                        {
                            await CreateFileCopier(new FileInfo(title.DiscIsoPath), new DirectoryInfo(dstPath)).CopyAsync(ct);

                            // RPCS3 loads an encrypted ISO when a matching-basename .dkey sits beside it.
                            if (title.DiscDkeyPath != null)
                                File.Copy(title.DiscDkeyPath, Path.Combine(dstPath, Path.GetFileName(title.DiscDkeyPath)), true);

                            installDir = dstPath;
                            romPath = Path.Combine(dstPath, Path.GetFileName(title.DiscIsoPath));
                        }
                        else // PS3_GAME folder
                        {
                            var folderName = new DirectoryInfo(title.DiscFolderPath).Name;
                            var destFolder = Path.Combine(dstPath, folderName);
                            await CreateFileCopier(new DirectoryInfo(title.DiscFolderPath), new DirectoryInfo(destFolder)).CopyAsync(ct);

                            installDir = destFolder;
                            romPath = Path.Combine(destFolder, "PS3_GAME", "USRDIR", "EBOOT.BIN");
                        }
                    }
                    else // Pkg base
                    {
                        var gameDir = Rpcs3Emulator.GetGameDir(mapping, title.TitleId)
                            ?? throw new Exception("Could not resolve the RPCS3 dev_hdd0 game directory. Check the emulator's install path.");

                        InstallPkg(title.BasePkgPath, gameDir, ct);

                        installDir = gameDir;
                        romPath = Path.Combine(gameDir, "USRDIR", "EBOOT.BIN");
                    }

                    // Updates, DLC and RAPs all live under dev_hdd0 regardless of base kind.
                    InstallPkgContent(mapping, title, ct);

                    var gamePath = MaybeMakePortable(romPath);
                    var resolvedInstallDir = MaybeMakePortable(installDir);

                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
                    {
                        InstallDirectory = resolvedInstallDir,
                        Roms = new List<GameRom>() { new GameRom(Game.Name, gamePath) },
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

        // Installs updates (ascending APP_VER), DLC, then copies RAP licenses into exdata.
        private void InstallPkgContent(Settings.EmulatorMapping mapping, Ps3Scanner.Ps3Title title, CancellationToken ct)
        {
            var hasPkgContent = title.Updates.Count > 0 || title.Dlcs.Count > 0;
            if (hasPkgContent)
            {
                var gameDir = Rpcs3Emulator.GetGameDir(mapping, title.TitleId);
                if (string.IsNullOrEmpty(gameDir) || string.IsNullOrEmpty(title.TitleId))
                {
                    _emuLibrary.Logger.Warn($"[PS3] Skipping updates/DLC for \"{Game.Name}\": no resolvable title id / dev_hdd0 game directory.");
                }
                else
                {
                    // Add-on content (updates + DLC) shares the base's dev_hdd0/game/<id> dir. Protect the
                    // base game's bootable root PARAM.SFO: a version-bumping HG update still replaces it, but a
                    // non-bootable (CATEGORY=GD) DLC — or oddly-packaged update — can't downgrade it and stop
                    // RPCS3 from treating the dir as a launchable game.
                    foreach (var update in title.Updates)
                    {
                        ct.ThrowIfCancellationRequested();
                        InstallPkg(update.FilePath, gameDir, ct, protectBootableRootParamSfo: true);
                    }
                    foreach (var dlc in title.Dlcs)
                    {
                        ct.ThrowIfCancellationRequested();
                        InstallPkg(dlc.FilePath, gameDir, ct, protectBootableRootParamSfo: true);
                    }
                }
            }

            if (title.RapPaths.Count > 0)
            {
                var exdata = Rpcs3Emulator.GetExdataDir(mapping);
                if (!string.IsNullOrEmpty(exdata))
                {
                    Directory.CreateDirectory(exdata);
                    foreach (var rap in title.RapPaths)
                    {
                        ct.ThrowIfCancellationRequested();
                        File.Copy(rap, Path.Combine(exdata, Path.GetFileName(rap)), true);
                    }
                }
            }
        }

        private static void InstallPkg(string pkgPath, string gameDir, CancellationToken ct, bool protectBootableRootParamSfo = false)
        {
            using (var pkg = Ps3Pkg.Open(pkgPath))
            {
                pkg.ExtractTo(gameDir, ct, protectBootableRootParamSfo);
            }
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
