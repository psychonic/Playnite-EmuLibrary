using Playnite.SDK.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.ThreeDs
{
    // 3DS is the content-store family (Family A): cumulative updates -> latest-only, no license files.
    // InstallBaseAsync handles both base shapes: a LooseFile base is a directly-bootable file copied verbatim
    // to DestinationPath (Azahar reads it, including the z-variants, as-is); a content-store base (.cia / CDN
    // folder) is decrypted into Azahar's SD title store. A loose title has no updates/DLC, so its per-item
    // hooks are no-ops; content-store updates/DLC install through the same SD installer.
    class ThreeDsInstallController : CompositeInstallController<ThreeDsTitle, ThreeDsContentRef>
    {
        private readonly ThreeDsGameInfo _gameInfo;
        private readonly ThreeDsScanner _scanner;

        private Settings.EmulatorMapping _mapping;
        private Azahar _azahar;
        private SdTitleInstaller _installer;
        private string _installDir;
        private string _romPath;

        internal ThreeDsInstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetThreeDsGameInfo();
            _scanner = emuLibrary.GetScanner(RomType.ThreeDs) as ThreeDsScanner;
            Name = string.Format("Install to {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        protected override UpdateInstallStrategy Strategy => UpdateInstallStrategy.InstallLatestUpdateOnly;
        protected override Func<ThreeDsContentRef, IComparable> VersionKey => x => x.Version;

        protected override ThreeDsTitle ResolveComposite(CancellationToken ct)
        {
            _mapping = _gameInfo.Mapping
                ?? throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            return _scanner.BuildTitle(_mapping, _gameInfo, ct)
                ?? throw new Exception($"Source content for \"{Game.Name}\" could not be found under \"{_mapping.SourcePath}\".");
        }

        protected override async Task InstallBaseAsync(ThreeDsTitle title, CancellationToken ct)
        {
            if (title.BaseKind == ThreeDsBaseKind.LooseFile)
            {
                await InstallLooseBaseAsync(title, ct);
                return;
            }

            // A CDN base whose ticket (.tik) is missing has no resolvable titlekey, so it would install but never
            // decrypt. Refuse it before touching the SD store; add the .tik beside the folder and rescan.
            if (title.BaseLicenseMissing)
                throw new Exception(
                    $"\"{Game.Name}\" is a CDN title that needs a ticket (.tik) to decrypt, but none was found " +
                    $"beside or inside \"{Path.GetFileName(title.BaseRef.SourcePath)}\". Add the .tik and rescan, then install.");

            // Content-store (.cia / CDN folder): decrypt the base into Azahar's SD title store.
            _azahar = new Azahar(_mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);
            _installer = _azahar.CreateInstaller(_mapping.SourcePath);

            var result = await Task.Run(() => _installer.InstallUnit(title.BaseRef, ct), ct);
            _installDir = _azahar.Paths.TitleDir(title.TitleId);
            _romPath = result.BootAppPath
                ?? throw new Exception($"No bootable content was produced for \"{Game.Name}\".");
        }

        private async Task InstallLooseBaseAsync(ThreeDsTitle title, CancellationToken ct)
        {
            var destRoot = _mapping.DestinationPathResolved
                ?? throw new Exception("Destination path cannot be resolved. Please try removing and re-adding the mapping.");
            Directory.CreateDirectory(destRoot);

            var sourceFile = title.BaseRef.SourcePath;
            await CreateFileCopier(new FileInfo(sourceFile), new DirectoryInfo(destRoot)).CopyAsync(ct);

            _installDir = destRoot;
            _romPath = Path.Combine(destRoot, Path.GetFileName(sourceFile));

            // A loose .cci/.3ds/.cxi may be NCCH-encrypted; Azahar's loader needs no_crypto. Decrypt the copy in
            // place (already-decrypted dumps and .3dsx/.z* are left untouched). If keys are unavailable, keep the
            // verbatim copy and let Azahar handle it.
            await MaybeDecryptLooseCopyAsync(_romPath, ct);
        }

        private async Task MaybeDecryptLooseCopyAsync(string installedPath, CancellationToken ct)
        {
            if (!ThreeDsLooseDecrypt.NeedsDecryption(installedPath))
                return;

            Crypto.ThreeDsKeys keys;
            try
            {
                keys = new Azahar(_mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache)
                    .LoadKeys(_mapping.SourcePath);
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[3DS] \"{Path.GetFileName(installedPath)}\" is encrypted but no keys were found; leaving the copy as-is for Azahar to decrypt.");
                return;
            }

            await Task.Run(() => ThreeDsLooseDecrypt.DecryptInPlace(installedPath, keys, _emuLibrary.Logger, ct), ct);
        }

        // Loose titles have no updates/DLC. Content-store updates/DLC are imported into their own SD title dirs.
        protected override async Task InstallUpdateAsync(ThreeDsTitle title, ThreeDsContentRef update, CancellationToken ct)
        {
            if (title.BaseKind == ThreeDsBaseKind.LooseFile)
                return;
            await Task.Run(() => _installer.InstallUnit(update, ct), ct);
        }

        protected override async Task InstallDlcAsync(ThreeDsTitle title, ThreeDsContentRef dlc, CancellationToken ct)
        {
            if (title.BaseKind == ThreeDsBaseKind.LooseFile)
                return;
            await Task.Run(() => _installer.InstallUnit(dlc, ct), ct);
        }

        protected override string GetInstallDir(ThreeDsTitle title) => _installDir;
        protected override string GetRomPath(ThreeDsTitle title) => _romPath;
        protected override string GetRomName(ThreeDsTitle title) => title.Name;
    }
}
