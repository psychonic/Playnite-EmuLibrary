using Playnite.SDK.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.Yuzu
{
    // Switch is the content-store family (Family A): updates are cumulative so only the latest installs, and
    // there are no license files. Base/update/DLC are all imported into the emulator's NAND.
    class YuzuInstallController : CompositeInstallController<YuzuTitle, string>
    {
        private readonly YuzuGameInfo _gameInfo;
        private readonly YuzuScanner _scanner;

        private Settings.EmulatorMapping _mapping;
        private Yuzu _yuzu;

        internal YuzuInstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetYuzuGameInfo();
            _scanner = emuLibrary.GetScanner(RomType.Yuzu) as YuzuScanner;
            Name = string.Format("Install to {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        protected override UpdateInstallStrategy Strategy => UpdateInstallStrategy.InstallLatestUpdateOnly;
        // YuzuTitle already carries at most one (the latest) update, so the key only needs to be stable.
        protected override Func<string, IComparable> VersionKey => x => x;

        protected override YuzuTitle ResolveComposite(CancellationToken ct)
        {
            _mapping = _gameInfo.Mapping
                ?? throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");
            _yuzu = new Yuzu(_mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);

            // Re-derive the title's composite from the source dir at install time (the source files may have
            // changed since the scan), exactly like PS3.
            return _scanner.BuildTitle(_mapping, _gameInfo.TitleId, ct)
                ?? throw new Exception($"Source content for \"{Game.Name}\" could not be found under \"{_mapping.SourcePath}\".");
        }

        protected override Task InstallBaseAsync(YuzuTitle title, CancellationToken ct) =>
            Task.Run(() => _yuzu.InstallFileToNand(title.ProgramFile, ByteProgressFor(title, title.ProgramFile)), ct);

        protected override Task InstallUpdateAsync(YuzuTitle title, string update, CancellationToken ct) =>
            Task.Run(() => _yuzu.InstallFileToNand(update, ByteProgressFor(title, update)), ct);

        protected override Task InstallDlcAsync(YuzuTitle title, string dlc, CancellationToken ct) =>
            Task.Run(() => _yuzu.InstallFileToNand(dlc, ByteProgressFor(title, dlc)), ct);

        // A byte reporter sized to the file's known NAND footprint, so this phase's bar shows a real
        // percentage. Null (indeterminate bar) when the size wasn't captured at scan time.
        private IProgress<long> ByteProgressFor(YuzuTitle title, string file) =>
            title.FileInstallSizes.TryGetValue(file, out var size) && size > 0
                ? CreateByteProgress((long)size)
                : null;

        protected override string GetInstallDir(YuzuTitle title) => _mapping.EmulatorBasePathResolved;
        protected override string GetRomPath(YuzuTitle title) => _yuzu.GetLaunchPathFromTitleId(_gameInfo.TitleId);
        protected override string GetRomName(YuzuTitle title) => title.Name;

        // A canceled/failed NAND import can leave partial content in the NAND; since installed-ness is derived
        // from the NAND each scan, remove it so the game doesn't resurface as installed. If we never got past
        // ResolveComposite (_yuzu null), nothing was imported yet.
        protected override void CleanupPartialInstall()
        {
            _yuzu?.UninstallTitleFromNand(_gameInfo.TitleId, false);
        }
    }
}
