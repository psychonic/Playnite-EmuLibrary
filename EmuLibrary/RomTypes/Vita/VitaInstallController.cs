using EmuLibrary.Util.Vita;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.Vita
{
    // PS Vita (Vita3K) is the disc+package family (Family B): the base installs into ux0/app/<id> and updates
    // install all-in-order (ascending APP_VER) into ux0/patch/<id>. The composite base owns the sequence; each
    // hook branches on the item's source kind:
    //   * Tree (NoNpDrm): copy the already-decrypted directory tree verbatim.
    //   * Pkg  (PSN): decrypt + extract the encrypted .pkg via VitaPkg.
    // DLC (PSN) extracts into ux0/addcont/<id>/<contentId>; licenses (PSN) write ux0/license/<id>/<cid>.rif
    // from the title's work.bin/zRIF via Zrif. NoNpDrm titles carry no DLC or licenses, so those hooks are
    // effectively no-ops for them.
    class VitaInstallController : CompositeInstallController<VitaTitle, VitaContentRef>
    {
        private readonly VitaGameInfo _gameInfo;
        private readonly VitaScanner _scanner;

        private Settings.EmulatorMapping _mapping;
        private string _installDir;
        private string _romPath;

        // ux0 dirs actually written this install (for partial-install cleanup), all keyed by title id.
        private string _appDir;
        private readonly List<string> _patchDirs = new List<string>();
        private string _addContBaseDir; // ux0/addcont/<id>
        private string _licenseDir;     // ux0/license/<id>

        internal VitaInstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetVitaGameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Vita) as VitaScanner;
            Name = string.Format("Install to {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        protected override UpdateInstallStrategy Strategy => UpdateInstallStrategy.InstallAllUpdatesInOrder;
        protected override Func<VitaContentRef, IComparable> VersionKey => x => x.AppVerParsed;

        protected override VitaTitle ResolveComposite(CancellationToken ct)
        {
            _mapping = _gameInfo.Mapping
                ?? throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            return _scanner.BuildTitle(_mapping, _gameInfo.SourceFullDir, _gameInfo.TitleId, ct)
                ?? throw new Exception($"Source content for \"{Game.Name}\" (title id \"{_gameInfo.TitleId}\") could not be found under \"{_gameInfo.SourceFullDir}\".");
        }

        protected override async Task InstallBaseAsync(VitaTitle title, CancellationToken ct)
        {
            _appDir = VitaEmulator.GetAppDir(_mapping, title.TitleId)
                ?? throw new Exception("Could not resolve Vita3K's ux0 app directory. Check the emulator's install path (its config.yml pref-path).");

            if (title.BaseRef.SourceKind == VitaSourceKind.Tree)
            {
                // Copy the already-decrypted app tree verbatim into ux0/app/<id> (MultiFile-style recursive copy).
                await CreateFileCopier(new DirectoryInfo(title.BaseTreeDir), new DirectoryInfo(_appDir)).CopyAsync(ct, CopyProgress);
            }
            else
            {
                // PSN: decrypt + extract the base .pkg into ux0/app/<id> (outer CTR + inner PFS via the license).
                ExtractPkg(title.BaseRef.PkgPath, _appDir, ct, title.BaseRef.WorkBinPath);
            }

            _installDir = _appDir;
            _romPath = Path.Combine(_appDir, "eboot.bin");
        }

        protected override async Task InstallUpdateAsync(VitaTitle title, VitaContentRef update, CancellationToken ct)
        {
            var patchDir = VitaEmulator.GetPatchDir(_mapping, title.TitleId)
                ?? throw new Exception("Could not resolve Vita3K's ux0 patch directory. Check the emulator's install path (its config.yml pref-path).");

            _patchDirs.Add(patchDir);

            if (update.SourceKind == VitaSourceKind.Tree)
                await CreateFileCopier(new DirectoryInfo(update.SourceTreeDir), new DirectoryInfo(patchDir)).CopyAsync(ct, CopyProgress);
            else
                // Updates carry no license of their own — the base title's klicensee covers the patch content.
                ExtractPkg(update.PkgPath, patchDir, ct, update.WorkBinPath ?? title.BaseRef?.WorkBinPath);
        }

        // PSN DLC: decrypt + extract into ux0/addcont/<id>/<contentId>. NoNpDrm titles have no DLC.
        protected override Task InstallDlcAsync(VitaTitle title, VitaContentRef dlc, CancellationToken ct)
        {
            if (dlc.SourceKind != VitaSourceKind.Pkg)
                return Task.CompletedTask;

            _addContBaseDir = VitaEmulator.GetAddContDir(_mapping, title.TitleId)
                ?? throw new Exception("Could not resolve Vita3K's ux0 addcont directory. Check the emulator's install path (its config.yml pref-path).");

            var contentId = string.IsNullOrEmpty(dlc.ContentId) ? "dlc" : dlc.ContentId;
            var dlcDir = Path.Combine(_addContBaseDir, contentId);
            ExtractPkg(dlc.PkgPath, dlcDir, ct, dlc.WorkBinPath);
            return Task.CompletedTask;
        }

        // PSN license: write ux0/license/<id>/<contentId>.rif from the title's work.bin/zRIF. NoNpDrm titles
        // have no separate license (the NoNpDrm work.bin travels inside the app tree).
        protected override Task InstallLicenseAsync(VitaTitle title, VitaContentRef license, CancellationToken ct)
        {
            if (license.SourceKind != VitaSourceKind.License || string.IsNullOrEmpty(license.WorkBinPath))
                return Task.CompletedTask;

            _licenseDir = VitaEmulator.GetLicenseDir(_mapping, title.TitleId)
                ?? throw new Exception("Could not resolve Vita3K's ux0 license directory. Check the emulator's install path (its config.yml pref-path).");
            Directory.CreateDirectory(_licenseDir);

            var rifBytes = Zrif.ReadRifFromWorkBin(license.WorkBinPath);
            var contentId = Zrif.GetContentId(rifBytes) ?? license.ContentId;
            if (string.IsNullOrEmpty(contentId))
                contentId = title.TitleId; // last resort so a license still lands somewhere inspectable

            File.WriteAllBytes(Path.Combine(_licenseDir, contentId + ".rif"), rifBytes);
            return Task.CompletedTask;
        }

        protected override string GetInstallDir(VitaTitle title) => _installDir;
        protected override string GetRomPath(VitaTitle title) => _romPath;
        protected override string GetRomName(VitaTitle title) => title.Name;

        // Decrypts + extracts a Vita .pkg into targetDir, sizing the progress bar to the encrypted data segment.
        // When a license (work.bin/zRIF) is available, its klicensee also drives inner PFS decryption so the
        // extracted tree is fully plaintext.
        private void ExtractPkg(string pkgPath, string targetDir, CancellationToken ct, string workBinPath = null)
        {
            using (var pkg = VitaPkg.Open(pkgPath, KlicenseeFromWorkBin(workBinPath)))
            {
                var progress = CreateByteProgress(pkg.DataSize);
                pkg.ExtractTo(targetDir, ct, progress);
            }
        }

        // Decodes the inner PFS klicensee from a title's work.bin/zRIF; null when absent/unreadable (the pkg then
        // extracts outer-layer only).
        private static byte[] KlicenseeFromWorkBin(string workBinPath)
        {
            if (string.IsNullOrEmpty(workBinPath) || !File.Exists(workBinPath))
                return null;
            try
            {
                return Zrif.GetKlicensee(Zrif.ReadRifFromWorkBin(workBinPath));
            }
            catch
            {
                return null;
            }
        }

        // A canceled/failed install can leave partial output. Mirror VitaUninstallController: delete the
        // ux0/app/<id>, ux0/patch/<id>, ux0/addcont/<id> and ux0/license/<id> dirs we created this run (all
        // keyed by title id and entirely ours). Uses paths resolved during install (Game.Roms/InstallDirectory
        // aren't set on a canceled install). ux0/user (save data) is never touched.
        protected override void CleanupPartialInstall()
        {
            DeleteDirectoryIfExists(_appDir);
            foreach (var dir in _patchDirs)
                DeleteDirectoryIfExists(dir);
            DeleteDirectoryIfExists(_addContBaseDir);
            DeleteDirectoryIfExists(_licenseDir);
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
