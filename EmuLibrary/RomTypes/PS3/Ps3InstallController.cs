using EmuLibrary.Util.Ps3;
using Playnite.SDK.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.Ps3
{
    // PS3 is the disc+package family (Family B): all updates install in ascending APP_VER order, and RAP
    // license files are copied into exdata. The base is either a disc image copy or a package extract; both
    // updates/DLC and RAPs land under dev_hdd0 regardless of base kind.
    class Ps3InstallController : CompositeInstallController<Ps3Scanner.Ps3Title, Ps3FileInfo>
    {
        private readonly Ps3GameInfo _gameInfo;
        private readonly Ps3Scanner _scanner;

        private Settings.EmulatorMapping _mapping;
        private string _installDir;
        private string _romPath;
        private string _gameDir; // dev_hdd0/game/<id>, where updates/DLC land; null if unresolvable

        internal Ps3InstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetPs3GameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Ps3) as Ps3Scanner;
            Name = string.Format("Install to {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        protected override UpdateInstallStrategy Strategy => UpdateInstallStrategy.InstallAllUpdatesInOrder;
        protected override Func<Ps3FileInfo, IComparable> VersionKey => x => x.AppVerParsed;

        protected override Ps3Scanner.Ps3Title ResolveComposite(CancellationToken ct)
        {
            _mapping = _gameInfo.Mapping
                ?? throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            if (!string.IsNullOrEmpty(_gameInfo.SourceIsoFileName))
            {
                var isoPath = Path.Combine(_mapping.SourcePath, _gameInfo.SourceIsoFileName);
                return _scanner.BuildLooseIsoTitle(isoPath, ct)
                    ?? throw new Exception($"Source ISO \"{_gameInfo.SourceIsoFileName}\" could not be found at \"{_mapping.SourcePath}\".");
            }

            return _scanner.BuildTitle(_mapping, _gameInfo.SourceFullDir, ct)
                ?? throw new Exception($"Source content for \"{Game.Name}\" could not be found under \"{_gameInfo.SourceFullDir}\".");
        }

        protected override async Task InstallBaseAsync(Ps3Scanner.Ps3Title title, CancellationToken ct)
        {
            if (title.BaseKind == Ps3BaseKind.Disc)
            {
                var dstPath = _mapping.DestinationPathResolved
                    ?? throw new Exception("Destination path cannot be resolved. Please try removing and re-adding the mapping.");
                Directory.CreateDirectory(dstPath);

                if (title.DiscIsoPath != null)
                {
                    await CreateFileCopier(new FileInfo(title.DiscIsoPath), new DirectoryInfo(dstPath)).CopyAsync(ct);

                    // RPCS3 loads an encrypted ISO when a matching-basename .dkey sits beside it.
                    if (title.DiscDkeyPath != null)
                        File.Copy(title.DiscDkeyPath, Path.Combine(dstPath, Path.GetFileName(title.DiscDkeyPath)), true);

                    _installDir = dstPath;
                    _romPath = Path.Combine(dstPath, Path.GetFileName(title.DiscIsoPath));
                }
                else // PS3_GAME folder
                {
                    var folderName = new DirectoryInfo(title.DiscFolderPath).Name;
                    var destFolder = Path.Combine(dstPath, folderName);
                    await CreateFileCopier(new DirectoryInfo(title.DiscFolderPath), new DirectoryInfo(destFolder)).CopyAsync(ct);

                    _installDir = destFolder;
                    _romPath = Path.Combine(destFolder, "PS3_GAME", "USRDIR", "EBOOT.BIN");
                }
            }
            else // Pkg base
            {
                _gameDir = Rpcs3Emulator.GetGameDir(_mapping, title.TitleId)
                    ?? throw new Exception("Could not resolve the RPCS3 dev_hdd0 game directory. Check the emulator's install path.");

                InstallPkg(title.BasePkgPath, _gameDir, ct);

                _installDir = _gameDir;
                _romPath = Path.Combine(_gameDir, "USRDIR", "EBOOT.BIN");
            }

            // Updates, DLC and RAPs all live under dev_hdd0 regardless of base kind. The pkg base already
            // resolved the game dir above; for a disc base resolve it here for any add-on content.
            if (string.IsNullOrEmpty(_gameDir))
                _gameDir = Rpcs3Emulator.GetGameDir(_mapping, title.TitleId);

            if ((title.Updates.Count > 0 || title.Dlcs.Count > 0) &&
                (string.IsNullOrEmpty(_gameDir) || string.IsNullOrEmpty(title.TitleId)))
            {
                _emuLibrary.Logger.Warn($"[PS3] Skipping updates/DLC for \"{Game.Name}\": no resolvable title id / dev_hdd0 game directory.");
            }
        }

        // Add-on content (updates + DLC) shares the base's dev_hdd0/game/<id> dir. Protect the base game's
        // bootable root PARAM.SFO: a version-bumping HG update still replaces it, but a non-bootable
        // (CATEGORY=GD) DLC — or oddly-packaged update — can't downgrade it and stop RPCS3 from treating the
        // dir as a launchable game.
        protected override Task InstallUpdateAsync(Ps3Scanner.Ps3Title title, Ps3FileInfo update, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_gameDir) && !string.IsNullOrEmpty(title.TitleId))
                InstallPkg(update.FilePath, _gameDir, ct, protectBootableRootParamSfo: true);
            return Task.CompletedTask;
        }

        protected override Task InstallDlcAsync(Ps3Scanner.Ps3Title title, Ps3FileInfo dlc, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_gameDir) && !string.IsNullOrEmpty(title.TitleId))
                InstallPkg(dlc.FilePath, _gameDir, ct, protectBootableRootParamSfo: true);
            return Task.CompletedTask;
        }

        protected override Task InstallLicenseAsync(Ps3Scanner.Ps3Title title, Ps3FileInfo license, CancellationToken ct)
        {
            var exdata = Rpcs3Emulator.GetExdataDir(_mapping);
            if (!string.IsNullOrEmpty(exdata))
            {
                Directory.CreateDirectory(exdata);
                File.Copy(license.FilePath, Path.Combine(exdata, Path.GetFileName(license.FilePath)), true);
            }
            return Task.CompletedTask;
        }

        protected override string GetInstallDir(Ps3Scanner.Ps3Title title) => _installDir;
        protected override string GetRomPath(Ps3Scanner.Ps3Title title) => _romPath;

        private static void InstallPkg(string pkgPath, string gameDir, CancellationToken ct, bool protectBootableRootParamSfo = false)
        {
            using (var pkg = Ps3Pkg.Open(pkgPath))
            {
                pkg.ExtractTo(gameDir, ct, protectBootableRootParamSfo);
            }
        }
    }
}
