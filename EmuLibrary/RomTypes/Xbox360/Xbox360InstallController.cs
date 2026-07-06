using Playnite.SDK.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.Xbox360
{
    // Xbox 360 (Xenia) is a third composite-content combination: disc/package base (copied to DestinationPath, like Family
    // B) + cumulative latest-only updates (like Family A) + no license slot. Every primitive is a plain copy:
    // the base .iso is copied to DestinationPath; the selected update + DLC are copied into Xenia's content
    // tree, where Xenia auto-applies them by (TitleId, MediaId) at boot.
    class Xbox360InstallController : CompositeInstallController<Xbox360Title, Xbox360ContentRef>
    {
        private readonly Xbox360GameInfo _gameInfo;
        private readonly Xbox360Scanner _scanner;

        private Settings.EmulatorMapping _mapping;
        private string _installDir;
        private string _romPath;

        internal Xbox360InstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetXbox360GameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Xbox360) as Xbox360Scanner;
            Name = string.Format("Install to {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        protected override UpdateInstallStrategy Strategy => UpdateInstallStrategy.InstallLatestUpdateOnly;
        protected override Func<Xbox360ContentRef, IComparable> VersionKey => x => x.Version;

        protected override Xbox360Title ResolveComposite(CancellationToken ct)
        {
            _mapping = _gameInfo.Mapping
                ?? throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            return _scanner.BuildTitle(_mapping, _gameInfo.TitleId, _gameInfo.MediaId, _gameInfo.SourceName, ct)
                ?? throw new Exception($"Source content for \"{Game.Name}\" could not be found under \"{_mapping.SourcePath}\".");
        }

        protected override async Task InstallBaseAsync(Xbox360Title title, CancellationToken ct)
        {
            var dstPath = _mapping.DestinationPathResolved
                ?? throw new Exception("Destination path cannot be resolved. Please try removing and re-adding the mapping.");
            Directory.CreateDirectory(dstPath);

            // Xenia opens the base in place — the .iso for a disc, the STFS container for a digital title, or
            // the .zar archive. Copy it verbatim to DestinationPath (no decrypt/extract); the primitive is
            // identical for all three.
            await CreateFileCopier(new FileInfo(title.BaseRef.SourcePath), new DirectoryInfo(dstPath)).CopyAsync(ct, CopyProgress);

            _installDir = dstPath;
            _romPath = Path.Combine(dstPath, Path.GetFileName(title.BaseRef.SourcePath));
        }

        protected override Task InstallUpdateAsync(Xbox360Title title, Xbox360ContentRef update, CancellationToken ct)
        {
            CopyIntoContentTree(update);
            return Task.CompletedTask;
        }

        protected override Task InstallDlcAsync(Xbox360Title title, Xbox360ContentRef dlc, CancellationToken ct)
        {
            CopyIntoContentTree(dlc);
            return Task.CompletedTask;
        }

        // Copies a TU/DLC STFS container into Xenia's content tree:
        //   {contentRoot}\0000000000000000\{TitleId:X8}\{ContentType:X8}\{ContentName}
        private void CopyIntoContentTree(Xbox360ContentRef unit)
        {
            var destDir = XeniaEmulator.GetContentTypeDir(_mapping, unit.TitleId, unit.ContentType)
                ?? throw new Exception(
                    "Could not resolve Xenia's content folder. Set the emulator's install path (or add a " +
                    "portable.txt beside xenia.exe) so updates and DLC can be installed.");
            Directory.CreateDirectory(destDir);
            File.Copy(unit.SourcePath, Path.Combine(destDir, unit.ContentName), true);
        }

        protected override string GetInstallDir(Xbox360Title title) => _installDir;
        protected override string GetRomPath(Xbox360Title title) => _romPath;
        protected override string GetRomName(Xbox360Title title) => title.Name;
    }
}
