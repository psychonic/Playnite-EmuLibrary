using EmuLibrary.RomTypes.WiiU.Crypto;
using EmuLibrary.Util.ZArchive;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.RomTypes.WiiU
{
    // Wii U is the content-store family (Family A): cumulative updates -> latest-only, no license files. But
    // unlike Yuzu (NAND import), the install primitive is "produce one launchable item at DestinationPath":
    // either copy a self-contained source verbatim, or decrypt + merge base/update/DLC into a single .wua.
    // The whole operation runs in InstallBaseAsync (the per-item hooks are no-ops) because a .wua is built
    // from all units at once. No NAND is ever touched.
    class WiiUInstallController : CompositeInstallController<WiiUTitle, WiiUContentRef>
    {
        private readonly WiiUGameInfo _gameInfo;
        private readonly WiiUScanner _scanner;

        private Settings.EmulatorMapping _mapping;
        private string _installDir;
        private string _romPath;

        internal WiiUInstallController(Game game, IEmuLibrary emuLibrary) : base(game, emuLibrary)
        {
            _gameInfo = game.GetWiiUGameInfo();
            _scanner = emuLibrary.GetScanner(RomType.WiiU) as WiiUScanner;
            Name = string.Format("Install to {0}", _gameInfo.Mapping?.Emulator?.Name ?? "Emulator");
        }

        protected override UpdateInstallStrategy Strategy => UpdateInstallStrategy.InstallLatestUpdateOnly;
        protected override Func<WiiUContentRef, IComparable> VersionKey => x => x.Version;

        protected override WiiUTitle ResolveComposite(CancellationToken ct)
        {
            _mapping = _gameInfo.Mapping
                ?? throw new Exception("Mapped emulator data cannot be found. Please try removing and re-adding.");

            return _scanner.BuildTitle(_mapping, _gameInfo.TitleId, ct)
                ?? throw new Exception($"Source content for \"{Game.Name}\" could not be found under \"{_mapping.SourcePath}\".");
        }

        protected override async Task InstallBaseAsync(WiiUTitle title, CancellationToken ct)
        {
            var destRoot = _mapping.DestinationPathResolved
                ?? throw new Exception("Destination path cannot be resolved. Please try removing and re-adding the mapping.");

            var destFolder = Path.Combine(destRoot, Cemu.DestinationFolderName(title.Name, title.TitleId));
            Directory.CreateDirectory(destFolder);

            if (title.IsSelfContained)
                await InstallSelfContainedAsync(title, destFolder, ct);
            else
                await ConvertToWuaAsync(title, destFolder, ct);

            _installDir = destFolder;
        }

        // Copy a directly-playable source verbatim into the destination (Cemu loads it via -g without any
        // decryption): a loadiine folder, or a disc image (.wux/.wud) plus its .key.
        private async Task InstallSelfContainedAsync(WiiUTitle title, string destFolder, CancellationToken ct)
        {
            var baseRef = title.BaseRef;
            switch (baseRef.Format)
            {
                case WiiUSourceFormat.Loadiine:
                    await CreateFileCopier(new DirectoryInfo(baseRef.SourcePath), new DirectoryInfo(destFolder)).CopyAsync(ct);
                    var rpx = Directory.GetFiles(Path.Combine(destFolder, "code"), "*.rpx", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    _romPath = rpx ?? throw new Exception($"No .rpx found in copied loadiine title \"{destFolder}\".");
                    break;

                case WiiUSourceFormat.Wua:
                    // A .wua is already a self-contained, decrypted bundle; Cemu loads it via -g. Just copy it.
                    await CreateFileCopier(new FileInfo(baseRef.SourcePath), new DirectoryInfo(destFolder)).CopyAsync(ct);
                    _romPath = Path.Combine(destFolder, Path.GetFileName(baseRef.SourcePath));
                    break;

                case WiiUSourceFormat.Wux:
                case WiiUSourceFormat.Wud:
                    await CreateFileCopier(new FileInfo(baseRef.SourcePath), new DirectoryInfo(destFolder)).CopyAsync(ct);
                    // Cemu needs the disc key beside the image (same basename .key) to load an encrypted disc.
                    var keyPath = Cemu.ResolveDiscKeyPath(baseRef.SourcePath)
                        ?? throw new Exception($"No .key file found beside \"{baseRef.SourcePath}\".");
                    File.Copy(keyPath, Path.Combine(destFolder, Path.GetFileNameWithoutExtension(baseRef.SourcePath) + ".key"), true);
                    _romPath = Path.Combine(destFolder, Path.GetFileName(baseRef.SourcePath));
                    break;

                default:
                    throw new NotSupportedException($"Self-contained install of {baseRef.Format} is not yet supported.");
            }
        }

        // Decrypt base + latest update + DLC and merge them into a single .wua at the destination. Each unit
        // may be an NUS dump or a disc game partition (e.g. a .wux base with separate NUS updates).
        private async Task ConvertToWuaAsync(WiiUTitle title, string destFolder, CancellationToken ct)
        {
            var commonKey = new Cemu(_mapping.EmulatorBasePathResolved, _emuLibrary.Logger).CommonKey;

            var refs = new List<WiiUContentRef> { title.BaseRef };
            if (title.UpdateRef != null)
                refs.Add(title.UpdateRef);
            refs.AddRange(title.DlcRefs);

            if (refs.Any(r => r.Format == WiiUSourceFormat.Loadiine))
                throw new NotSupportedException(
                    "Converting a loadiine title (or a loadiine base with separate updates/DLC) to .wua is not yet " +
                    "supported. Provide the base as an NUS dump or disc image.");

            if (refs.Any(r => r.Format == WiiUSourceFormat.Wua))
                throw new NotSupportedException(
                    "Merging separate updates/DLC into an existing .wua is not supported. A .wua already bundles its " +
                    "content; provide the update/DLC as part of the .wua, or use NUS/disc sources for the whole title.");

            var wuaPath = Path.Combine(destFolder, $"{SanitizeFileName(title.Name)}.wua");

            await Task.Run(() =>
            {
                var discs = new List<WiiUDisc>();
                var readers = new List<NusReader>();
                try
                {
                    foreach (var r in refs)
                    {
                        if (r.Format == WiiUSourceFormat.Nus)
                        {
                            readers.Add(new NusReader(r.SourcePath, commonKey));
                        }
                        else // Wux / Wud
                        {
                            var keyPath = Cemu.ResolveDiscKeyPath(r.SourcePath)
                                ?? throw new Exception($"No .key file found beside \"{r.SourcePath}\".");
                            var disc = WiiUDisc.Open(r.SourcePath, keyPath);
                            discs.Add(disc);
                            var part = disc.GamePartitions.FirstOrDefault(p => p.TitleId == r.TitleId)
                                ?? disc.GamePartitions.FirstOrDefault()
                                ?? throw new Exception($"No game partition found in \"{r.SourcePath}\".");
                            readers.Add(new NusReader(new WudContentSource(disc.Reader, part), commonKey));
                        }
                    }

                    WuaBuilder.Build(wuaPath, readers, ct);
                }
                finally
                {
                    foreach (var rdr in readers)
                        rdr.Dispose();
                    foreach (var d in discs)
                        d.Dispose();
                }
            }, ct);

            _romPath = wuaPath;
        }

        // The whole composite is installed in InstallBaseAsync, so the per-item hooks are no-ops.
        protected override Task InstallUpdateAsync(WiiUTitle title, WiiUContentRef update, CancellationToken ct) => Task.CompletedTask;
        protected override Task InstallDlcAsync(WiiUTitle title, WiiUContentRef dlc, CancellationToken ct) => Task.CompletedTask;

        protected override string GetInstallDir(WiiUTitle title) => _installDir;
        protected override string GetRomPath(WiiUTitle title) => _romPath;
        protected override string GetRomName(WiiUTitle title) => title.Name;

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "game";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
