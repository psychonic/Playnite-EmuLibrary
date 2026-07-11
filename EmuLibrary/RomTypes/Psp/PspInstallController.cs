using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.Psp
{
    // PSP (PPSSPP) is the disc+package family (Family B): the base disc image is copied to DestinationPath and
    // any update/DLC payload folders install all-in-order into the emulator's memory stick under
    // PSP/GAME/<titleId>. There are no license files (PSP EDAT is keyed to the console — decryption is PPSSPP's
    // job; we only copy).
    class PspInstallController : CompositeInstallController<PspTitle, PspContentRef>
    {
        private readonly PspGameInfo _gameInfo;
        private readonly PspScanner _scanner;

        private Settings.EmulatorMapping _mapping;
        private string _installDir;
        private string _romPath;

        // Memory-stick payload dirs actually written this install (for partial-install cleanup).
        private readonly List<string> _writtenContentDirs = new List<string>();

        internal PspInstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetPspGameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Psp) as PspScanner;
            Name = string.Format("Install to {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        protected override UpdateInstallStrategy Strategy => UpdateInstallStrategy.InstallAllUpdatesInOrder;
        protected override Func<PspContentRef, IComparable> VersionKey => x => x.Version;

        protected override PspTitle ResolveComposite(CancellationToken ct)
        {
            _mapping = _gameInfo.Mapping
                ?? throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            return _scanner.BuildTitle(_mapping, _gameInfo.SourceFullPath, ct)
                ?? throw new Exception($"Source image for \"{Game.Name}\" could not be found at \"{_gameInfo.SourceFullPath}\".");
        }

        protected override async Task InstallBaseAsync(PspTitle title, CancellationToken ct)
        {
            var dstPath = _mapping.DestinationPathResolved
                ?? throw new Exception("Destination path cannot be resolved. Please try removing and re-adding the mapping.");
            Directory.CreateDirectory(dstPath);

            // PPSSPP opens the image in place; copy it verbatim (no decompress/extract).
            await CreateFileCopier(new FileInfo(title.SourceImagePath), new DirectoryInfo(dstPath)).CopyAsync(ct, CopyProgress);

            _installDir = dstPath;
            _romPath = Path.Combine(dstPath, Path.GetFileName(title.SourceImagePath));

            if ((title.Updates.Count > 0 || title.Dlcs.Count > 0)
                && string.IsNullOrEmpty(PpssppEmulator.GetMemstickRoot(_mapping)))
            {
                throw new Exception(
                    $"\"{Game.Name}\" has update/DLC content, but PPSSPP's memory-stick folder could not be " +
                    "resolved from the emulator's install path. Set the emulator path (or configure the memstick " +
                    "location) and try again.");
            }
        }

        protected override Task InstallUpdateAsync(PspTitle title, PspContentRef update, CancellationToken ct) =>
            CopyPayloadAsync(update, ct);

        protected override Task InstallDlcAsync(PspTitle title, PspContentRef dlc, CancellationToken ct) =>
            CopyPayloadAsync(dlc, ct);

        // Copies a payload folder verbatim into <memstick>/PSP/GAME/<titleId> (or /SAVEDATA when routed).
        private async Task CopyPayloadAsync(PspContentRef item, CancellationToken ct)
        {
            var destDir = PpssppEmulator.GetContentDir(_mapping, item)
                ?? throw new Exception("Could not resolve PPSSPP's memory-stick folder for the update/DLC payload.");

            Directory.CreateDirectory(destDir);
            _writtenContentDirs.Add(destDir);
            await CreateFileCopier(new DirectoryInfo(item.SourcePath), new DirectoryInfo(destDir)).CopyAsync(ct, CopyProgress);
        }

        protected override string GetInstallDir(PspTitle title) => _installDir;
        protected override string GetRomPath(PspTitle title) => _romPath;
        protected override string GetRomName(PspTitle title) => title.Name;

        // A canceled/failed install can leave a partial image copy and partially-copied payload folders. Mirror
        // PspUninstallController: delete the copied image and the memory-stick payload dirs we created this run.
        // Uses paths resolved during install (Game.Roms/InstallDirectory aren't set on a canceled install).
        protected override void CleanupPartialInstall()
        {
            if (!string.IsNullOrEmpty(_romPath) && File.Exists(_romPath))
                File.Delete(_romPath);

            foreach (var dir in _writtenContentDirs)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }
    }
}
