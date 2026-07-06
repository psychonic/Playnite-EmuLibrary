using EmuLibrary.PlayniteCommon;
using EmuLibrary.RomTypes.Xbox360.Disc;
using EmuLibrary.RomTypes.Xbox360.Stfs;
using EmuLibrary.Settings;
using EmuLibrary.Util.Metadata;
using EmuLibrary.Util.ScanCache;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.Xbox360
{
    internal class Xbox360Scanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;

        // Per-mapping snapshot of the units parsed by the last full scan (GetGames). Reused by BuildTitle at
        // install time so it doesn't re-walk the whole source tree over SMB just to resolve one title — that
        // walk is O(all title folders) and dominates the install "Preparing" phase for a large digital library.
        // Updates/DLC are read from here (captured wherever the scan found them, incl. a separate MediaID tree);
        // the base is re-validated fresh from its known path. Populated on every scan, so it self-refreshes.
        private readonly ConcurrentDictionary<Guid, (List<Xbox360UnitInfo> Disc, List<Xbox360UnitInfo> Stfs)> _lastScanByMapping =
            new ConcurrentDictionary<Guid, (List<Xbox360UnitInfo>, List<Xbox360UnitInfo>)>();

        public override RomType RomType => RomType.Xbox360;

        // No legacy standalone Xbox 360 extension to migrate from.
        public override Guid LegacyPluginId => Guid.Parse("C5F3E8A1-7B2D-4E6F-9A1C-3D8E5F0B2A47");

        // Xbox 360 scans by its own format logic (XISO/XEX/STFS), not the emulator profile's image extensions.
        public override bool RequiresProfileImageExtensions => false;

        // The disc base is copied to DestinationPath (updates/DLC go to the Xenia content tree separately).
        public override bool RequiresDestinationPath => true;

        // How much of a default.xex to read for the execution-id parse. The XEX header + optional-header table
        // + execution id all sit near the start; 256 KiB is comfortably enough and still a small SMB read.
        private const int XexHeaderReadBytes = 0x40000;

        // Cap for the full default.xex read done only when extracting a disc/ZAR title image (the image lives in
        // an XDBF resource inside the whole PE basefile, not just the header). Bounds a pathological allocation;
        // real 360 executables are well under this.
        private const int XexFullReadBytes = 64 * 1024 * 1024;

        // Extensions that are never an STFS container — skipped before the magic sniff to cut SMB round-trips.
        private static readonly HashSet<string> NonStfsExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "iso", "zar", "7z", "zip", "rar", "gz", "txt", "nfo", "log", "md5", "sha1", "sfv", "dat", "xml",
            "html", "htm", "url", "jpg", "jpeg", "png", "gif", "bmp", "ini", "db", "dkey", "key", "exe", "dll",
        };

        public Xbox360Scanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        // Locator + metadata for one content unit, cached (JSON) keyed by the file's stamp.
        internal sealed class Xbox360UnitInfo
        {
            public string Path { get; set; }
            public Xbox360ContentKind Kind { get; set; }
            public uint TitleId { get; set; }
            public uint MediaId { get; set; }
            public uint Version { get; set; }
            public uint ContentType { get; set; }
            public ulong ContentSize { get; set; }

            // STFS embedded title name — used to name digital bases (their on-disk leaf is a hex content id,
            // so the filename is useless). Null for disc bases (their No-Intro filename is the name) and TU/DLC.
            public string DisplayName { get; set; }

            // Local path to the extracted STFS package thumbnail PNG (digital bases only), or null. The PNG is
            // written once under the plugin's data dir; this is just the reference to it.
            public string ThumbnailPath { get; set; }
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            if (string.IsNullOrEmpty(mapping.SourcePath) || !Directory.Exists(mapping.SourcePath))
            {
                _emuLibrary.Logger.Warn($"[X360] Source path \"{mapping.SourcePath}\" is empty or does not exist; nothing to scan for this mapping.");
                yield break;
            }

            var dstPath = mapping.DestinationPathResolved;
            _emuLibrary.Logger.Info($"[X360] Scanning source \"{mapping.SourcePath}\" (destination \"{dstPath}\").");

            // Title-id -> name DB (xenia-manager/x360db). Shadows the file-derived name only below the STFS
            // DisplayName (STFS > DB > filename). Loaded once, refreshed daily, fail-soft.
            var titleDb = new Xbox360Db(_emuLibrary);

            var parsed = EnumerateAndParse(mapping, args.CancelToken);

            // Record this full scan so a subsequent install can resolve its title without re-walking the tree.
            _lastScanByMapping[mapping.MappingId] = parsed;

            var updates = parsed.Stfs.Where(u => u.Kind == Xbox360ContentKind.TitleUpdate).ToList();
            var dlc = parsed.Stfs.Where(u => u.Kind == Xbox360ContentKind.Dlc).ToList();
            var digitalBases = parsed.Stfs.Where(u => u.Kind == Xbox360ContentKind.DigitalBase).ToList();

            // Disc/ZAR title-image extraction is gated (a full-executable read per title over SMB); default on.
            var extractDiscIcons = global::EmuLibrary.Settings.Settings.Instance?.ExtractDiscIcons ?? true;

            var bases = SelectScanBases(parsed.Disc, digitalBases);
            int shadowed = parsed.Disc.Count + digitalBases.Count - bases.Count;
            if (shadowed > 0)
                _emuLibrary.Logger.Debug($"[X360] {shadowed} digital base(s) shadowed by a disc of the same (TitleId, MediaId).");

            foreach (var baseUnit in bases)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                var title = ComposeTitle(baseUnit, updates, dlc);
                _emuLibrary.Logger.Debug($"[X360] Title \"{title.Name}\" ({title.TitleId:X8}/{title.MediaId:X8}) base={title.BaseKind} updates={title.Updates.Count} dlc={title.DlcRefs.Count} from \"{baseUnit.Path}\".");

                var gameInfo = new Xbox360GameInfo()
                {
                    MappingId = mapping.MappingId,
                    TitleId = title.TitleId,
                    MediaId = title.MediaId,
                    SourceName = RelativePath(baseUnit.Path, mapping.SourcePath),
                    BaseKind = title.BaseKind,
                };

                bool isInstalled = IsInstalled(mapping, title, dstPath, out var romPath, out var installDir);

                var roms = new List<GameRom>();
                if (isInstalled && !string.IsNullOrEmpty(romPath))
                    roms.Add(new GameRom(title.Name, MaybeMakePortable(romPath)));

                var game = new GameMetadata()
                {
                    Source = EmuLibrary.SourceName,
                    Name = title.Name,
                    IsInstalled = isInstalled,
                    GameId = gameInfo.AsGameId(),
                    InstallDirectory = isInstalled ? MaybeMakePortable(installDir) : null,
                    Roms = roms,
                    Version = title.Version,
                    InstallSize = title.InstallSize,
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    GameActions = new List<GameAction>()
                    {
                        new GameAction()
                        {
                            Name = $"Play in {mapping.Emulator.Name}",
                            Type = GameActionType.Emulator,
                            EmulatorId = mapping.EmulatorId,
                            EmulatorProfileId = mapping.EmulatorProfileId,
                            IsPlayAction = true,
                        }
                    }
                };

                // Name resolution STFS DisplayName > x360db > filename: the title already carries the STFS
                // DisplayName (digital) or filename (disc); only let the DB name shadow a plain filename.
                var stfsMeta = string.IsNullOrEmpty(baseUnit.DisplayName)
                    ? null
                    : new ExternalGameMetadata { Name = baseUnit.DisplayName };
                titleDb.TryGet(title.TitleId, out var dbMeta);
                ExternalGameMetadata.Coalesce(stfsMeta, dbMeta).ApplyTo(game);

                // The title's icon: the extracted STFS package thumbnail for digital bases, or the XEX-embedded
                // title image for disc/ZAR bases (gated, extracted once per title id via the shared cache).
                if (!string.IsNullOrEmpty(title.IconPath) && File.Exists(title.IconPath))
                {
                    game.Icon = new MetadataFile(title.IconPath);
                }
                else if (extractDiscIcons &&
                    (baseUnit.Kind == Xbox360ContentKind.DiscImage || baseUnit.Kind == Xbox360ContentKind.ZarImage))
                {
                    var iconPath = IconCache.EnsureIcon(
                        _emuLibrary.GetPluginUserDataPath(), "x360-thumbs", title.TitleId.ToString("X8"), "png",
                        () => ExtractDiscTitleImagePng(baseUnit), _emuLibrary.Logger);
                    if (!string.IsNullOrEmpty(iconPath))
                        game.Icon = new MetadataFile(iconPath);
                }

                yield return game;
            }
        }

        // Re-derives one title's composite from the source (used by the install/uninstall controllers — the
        // source may have changed since the scan). Matches the base by (TitleId, MediaId) and, when several
        // share that key, by SourceName. Returns null if the base no longer exists.
        //
        // Fast path: reuse the units from this mapping's last full scan instead of re-walking the entire source
        // tree over SMB (that walk is O(all title folders) and is what makes the install "Preparing" phase hang
        // for a large digital library). Updates/DLC come from the snapshot (captured wherever the scan found
        // them); the base is re-validated fresh from its known path (a single read, not a walk), so a base that
        // moved or changed identity since the scan still forces the full re-scan below. Newly-added updates/DLC
        // aren't seen until the next library scan — the usual "scan to pick up new content" workflow.
        internal Xbox360Title BuildTitle(EmulatorMapping mapping, uint titleId, uint mediaId, string sourceName, CancellationToken ct)
        {
            if (_lastScanByMapping.TryGetValue(mapping.MappingId, out var snapshot))
            {
                var fromSnapshot = TryComposeFromSnapshot(mapping, snapshot, titleId, mediaId, sourceName);
                if (fromSnapshot != null)
                    return fromSnapshot;
            }

            var parsed = EnumerateAndParse(mapping, ct);
            return ComposeFromUnits(mapping, parsed.Disc, parsed.Stfs, titleId, mediaId, sourceName);
        }

        // Locates the base for (titleId, mediaId) among an already-parsed unit set and composes its title with
        // the set's updates (latest-only) and DLC. Searches both disc and digital bases (no prefer-disc dedup —
        // SourceName disambiguates a disc from a digital release sharing a (TitleId, MediaId)). Null if no base.
        private Xbox360Title ComposeFromUnits(EmulatorMapping mapping, IReadOnlyList<Xbox360UnitInfo> disc,
            IReadOnlyList<Xbox360UnitInfo> stfs, uint titleId, uint mediaId, string sourceName)
        {
            var baseUnit = FindBase(mapping, disc, stfs, titleId, mediaId, sourceName);
            if (baseUnit == null)
                return null;

            var updates = stfs.Where(u => u.Kind == Xbox360ContentKind.TitleUpdate).ToList();
            var dlc = stfs.Where(u => u.Kind == Xbox360ContentKind.Dlc).ToList();
            return ComposeTitle(baseUnit, updates, dlc);
        }

        private Xbox360UnitInfo FindBase(EmulatorMapping mapping, IEnumerable<Xbox360UnitInfo> disc,
            IEnumerable<Xbox360UnitInfo> stfs, uint titleId, uint mediaId, string sourceName)
        {
            var allBases = disc.Concat(stfs.Where(u => u.Kind == Xbox360ContentKind.DigitalBase));
            var candidates = allBases.Where(u => u.TitleId == titleId && u.MediaId == mediaId).ToList();
            if (candidates.Count == 0)
                return null;

            return candidates.Count == 1
                ? candidates[0]
                : candidates.FirstOrDefault(u => string.Equals(RelativePath(u.Path, mapping.SourcePath), sourceName, StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0];
        }

        // Composes a title from the last scan's snapshot, re-validating the recorded base directly (re-parsing
        // it from its known path — a cache-hit-cheap single read, not a walk). Returns null to fall back to a
        // full re-scan when the base is gone or its identity changed since the scan.
        private Xbox360Title TryComposeFromSnapshot(EmulatorMapping mapping,
            (List<Xbox360UnitInfo> Disc, List<Xbox360UnitInfo> Stfs) snapshot,
            uint titleId, uint mediaId, string sourceName)
        {
            var recordedBase = FindBase(mapping, snapshot.Disc, snapshot.Stfs, titleId, mediaId, sourceName);
            if (recordedBase == null)
                return null;

            var freshBase = ReparseBase(recordedBase);
            if (freshBase == null || freshBase.TitleId != titleId || freshBase.MediaId != mediaId)
                return null;

            var updates = snapshot.Stfs.Where(u => u.Kind == Xbox360ContentKind.TitleUpdate).ToList();
            var dlc = snapshot.Stfs.Where(u => u.Kind == Xbox360ContentKind.Dlc).ToList();
            return ComposeTitle(freshBase, updates, dlc);
        }

        // Re-parses a single base unit from its known path (to freshly re-validate the snapshot's base without a
        // tree walk). Uses the same cached parse as the scan, so an unchanged base is a cheap cache hit. Null if
        // the file no longer exists or no longer parses as a base.
        private Xbox360UnitInfo ReparseBase(Xbox360UnitInfo recordedBase)
        {
            if (string.IsNullOrEmpty(recordedBase.Path) || !File.Exists(recordedBase.Path))
                return null;

            var file = new FileInfoWrapper(new FileInfo(recordedBase.Path));
            switch (recordedBase.Kind)
            {
                case Xbox360ContentKind.DiscImage: return GetDiscUnit(file);
                case Xbox360ContentKind.ZarImage: return GetZarUnit(file);
                default: return GetStfsUnit(file);
            }
        }

        // Groups one disc base with its (TitleId, MediaId)-matched updates (latest-only) and its TitleId-matched
        // DLC into a composite. The DLC match is by TitleId only (DLC packages may carry MediaId 0); updates
        // require the MediaId match (the TU share is foldered by MediaId).
        internal static Xbox360Title ComposeTitle(Xbox360UnitInfo baseUnit, List<Xbox360UnitInfo> updates, List<Xbox360UnitInfo> dlc)
        {
            var matchingUpdateRefs = updates
                .Where(u => u.TitleId == baseUnit.TitleId && u.MediaId == baseUnit.MediaId)
                .Select(ToRef);

            var selectedUpdates = CompositeContent
                .SelectUpdatesToInstall(matchingUpdateRefs, UpdateInstallStrategy.InstallLatestUpdateOnly, x => x.Version)
                .ToList();

            var dlcRefs = dlc
                .Where(d => d.TitleId == baseUnit.TitleId)
                .GroupBy(d => Path.GetFileName(d.Path), StringComparer.OrdinalIgnoreCase)
                .Select(g => ToRef(g.First()))
                .ToList();

            var baseRef = ToRef(baseUnit);

            Xbox360BaseKind baseKind;
            switch (baseUnit.Kind)
            {
                case Xbox360ContentKind.DigitalBase:
                    baseKind = Xbox360BaseKind.DigitalPackage;
                    break;
                case Xbox360ContentKind.ZarImage:
                    baseKind = Xbox360BaseKind.ZarArchive;
                    break;
                default:
                    baseKind = Xbox360BaseKind.DiscImage;
                    break;
            }

            // Disc bases carry good No-Intro filenames; digital bases sit under a hex content-id leaf, so their
            // only human-readable name is the STFS DisplayName (fall back to the filename, then the title id).
            string name;
            if (baseKind == Xbox360BaseKind.DigitalPackage && !string.IsNullOrEmpty(baseUnit.DisplayName))
                name = baseUnit.DisplayName;
            else
                name = StringExtensions.NormalizeGameName(
                    StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(baseUnit.Path)));

            ulong size = baseUnit.ContentSize
                + (ulong)selectedUpdates.Sum(u => (decimal)u.ContentSize)
                + (ulong)dlcRefs.Sum(d => (decimal)d.ContentSize);

            var latestUpdate = selectedUpdates.LastOrDefault();
            var version = (latestUpdate?.Version ?? baseUnit.Version).ToString();

            return new Xbox360Title()
            {
                TitleId = baseUnit.TitleId,
                MediaId = baseUnit.MediaId,
                Name = string.IsNullOrEmpty(name) ? baseUnit.TitleId.ToString("X8") : name,
                Version = version,
                BaseKind = baseKind,
                IconPath = baseUnit.ThumbnailPath,
                InstallSize = size,
                BaseRef = baseRef,
                Updates = selectedUpdates,
                DlcRefs = dlcRefs,
            };
        }

        // Merges disc and digital bases for the scan, deduping on (TitleId, MediaId): when a disc and a digital
        // release of the same title share that key, prefer the disc (the more complete dump). Two digital bases
        // sharing a key are both kept (each becomes its own Playnite entry, distinguished by SourceName).
        internal static List<Xbox360UnitInfo> SelectScanBases(List<Xbox360UnitInfo> disc, List<Xbox360UnitInfo> digitalBases)
        {
            var bases = new List<Xbox360UnitInfo>(disc);
            var discKeys = new HashSet<(uint TitleId, uint MediaId)>(disc.Select(d => (d.TitleId, d.MediaId)));
            foreach (var dig in digitalBases)
            {
                if (discKeys.Contains((dig.TitleId, dig.MediaId)))
                    continue;
                bases.Add(dig);
            }
            return bases;
        }

        private static Xbox360ContentRef ToRef(Xbox360UnitInfo u) => new Xbox360ContentRef
        {
            SourcePath = u.Path,
            Kind = u.Kind,
            TitleId = u.TitleId,
            MediaId = u.MediaId,
            Version = u.Version,
            ContentType = u.ContentType,
            ContentName = Path.GetFileName(u.Path),
            ContentSize = u.ContentSize,
        };

        // Installed iff the base copy exists in DestinationPath AND each selected update/DLC exists at its
        // content-tree dest (Ps3/WiiU model — installed-state derived from disk, never cached).
        private bool IsInstalled(EmulatorMapping mapping, Xbox360Title title, string dstPath, out string romPath, out string installDir)
        {
            romPath = null;
            installDir = null;

            if (string.IsNullOrEmpty(dstPath))
                return false;

            var installedBase = Path.Combine(dstPath, Path.GetFileName(title.BaseRef.SourcePath));
            installDir = dstPath;
            romPath = installedBase;
            if (!File.Exists(installedBase))
                return false;

            var contentRoot = XeniaEmulator.GetContentRoot(mapping);
            foreach (var unit in title.Updates.Concat(title.DlcRefs))
            {
                if (contentRoot == null)
                    return false;
                var dest = Path.Combine(XeniaEmulator.GetContentTypeDir(contentRoot, unit.TitleId, unit.ContentType), unit.ContentName);
                if (!File.Exists(dest))
                    return false;
            }

            return true;
        }

        // Enumerates the source once, then parses every disc base (.iso and .zar) and every STFS candidate
        // (TU/DLC) in parallel through the scan concurrency governor. All parse paths are stateless across calls
        // and the IScanCache is internally locked, so the workers are independent. .zar bases are folded into the
        // Disc list (they are full extracted dumps, copied verbatim just like an .iso, and dedup/prefer over a
        // digital release of the same key exactly as a disc does).
        private (List<Xbox360UnitInfo> Disc, List<Xbox360UnitInfo> Stfs) EnumerateAndParse(EmulatorMapping mapping, CancellationToken ct)
        {
            var disc = new List<Xbox360UnitInfo>();
            var stfs = new List<Xbox360UnitInfo>();

            if (string.IsNullOrEmpty(mapping.SourcePath) || !Directory.Exists(mapping.SourcePath))
                return (disc, stfs);

            var isoFiles = new List<FileSystemInfoBase>();
            var zarFiles = new List<FileSystemInfoBase>();
            var stfsCandidates = new List<FileSystemInfoBase>();

            foreach (var entry in new SafeFileEnumerator(mapping.SourcePath, "*.*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested)
                    return (disc, stfs);
                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    continue;

                var ext = entry.Extension.TrimStart('.').ToLowerInvariant();
                if (ext == "iso")
                {
                    isoFiles.Add(entry);
                }
                else if (ext == "zar")
                {
                    zarFiles.Add(entry);
                }
                else if (!NonStfsExtensions.Contains(ext) && IsStfsCandidateSize(entry))
                {
                    stfsCandidates.Add(entry);
                }
            }

            disc.AddRange(RunInspections(isoFiles, mapping.SourcePath, GetDiscUnit, ct).Where(u => u != null));
            disc.AddRange(RunInspections(zarFiles, mapping.SourcePath, GetZarUnit, ct).Where(u => u != null));
            stfs.AddRange(RunInspections(stfsCandidates, mapping.SourcePath, GetStfsUnit, ct).Where(u => u != null));

            return (disc, stfs);
        }

        private static bool IsStfsCandidateSize(FileSystemInfoBase entry)
        {
            var fi = entry as FileInfoBase;
            return fi == null || fi.Length >= StfsPackage.HeaderReadSize;
        }

        private static ulong FileLength(FileSystemInfoBase file)
        {
            var fi = file as FileInfoBase;
            return fi != null ? (ulong)fi.Length : 0UL;
        }

        // Reads + caches a disc .iso's identity (XISO -> default.xex -> XEX execution id). Nulls aren't cached.
        private Xbox360UnitInfo GetDiscUnit(FileSystemInfoBase file)
        {
            var stamp = FileStamp.FromFileSystemInfo(file);
            var cache = _emuLibrary.ScanCache;
            if (cache != null && cache.TryGet<Xbox360UnitInfo>(file.FullName, stamp, out var cached))
                return cached;

            Xbox360UnitInfo info = null;
            try
            {
                using (var reader = XdvdfsReader.TryOpen(file.FullName))
                {
                    if (reader == null)
                    {
                        _emuLibrary.Logger.Warn($"[X360] \"{file.FullName}\" is not a recognizable XGD/XISO volume; skipping.");
                        return null;
                    }

                    var xexBytes = reader.TryReadDefaultXex(XexHeaderReadBytes);
                    if (xexBytes == null || !XexHeader.TryParse(xexBytes, out var xex))
                    {
                        _emuLibrary.Logger.Warn($"[X360] Could not read default.xex execution id from \"{file.FullName}\"; skipping.");
                        return null;
                    }

                    info = new Xbox360UnitInfo()
                    {
                        Path = file.FullName,
                        Kind = Xbox360ContentKind.DiscImage,
                        TitleId = xex.TitleId,
                        MediaId = xex.MediaId,
                        Version = xex.Version,
                        ContentType = 0,
                        ContentSize = FileLength(file),
                    };
                }
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[X360] Failed to read disc \"{file.FullName}\".");
            }

            if (info != null)
                cache?.Set(file.FullName, stamp, info);
            return info;
        }

        // Reads + caches a Xenia .zar base's identity from its root default.xex (a ZArchive of an extracted,
        // decrypted dump — Xenia mounts it at the volume root and boots \default.xex). Same plaintext XEX2
        // execution-id parse as the disc path, just sourced from inside the archive. Nulls aren't cached.
        private Xbox360UnitInfo GetZarUnit(FileSystemInfoBase file)
        {
            var stamp = FileStamp.FromFileSystemInfo(file);
            var cache = _emuLibrary.ScanCache;
            if (cache != null && cache.TryGet<Xbox360UnitInfo>(file.FullName, stamp, out var cached))
                return cached;

            Xbox360UnitInfo info = null;
            try
            {
                if (!Xbox360ZarReader.TryReadDefaultXex(file.FullName, XexHeaderReadBytes, out var xexBytes) || xexBytes == null)
                {
                    _emuLibrary.Logger.Warn($"[X360] \"{file.FullName}\" is not a ZArchive with a top-level default.xex; skipping.");
                    return null;
                }

                if (!XexHeader.TryParse(xexBytes, out var xex))
                {
                    _emuLibrary.Logger.Warn($"[X360] Could not read default.xex execution id from zar \"{file.FullName}\"; skipping.");
                    return null;
                }

                info = new Xbox360UnitInfo()
                {
                    Path = file.FullName,
                    Kind = Xbox360ContentKind.ZarImage,
                    TitleId = xex.TitleId,
                    MediaId = xex.MediaId,
                    Version = xex.Version,
                    ContentType = 0,
                    ContentSize = FileLength(file),
                };
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[X360] Failed to read zar \"{file.FullName}\".");
            }

            if (info != null)
                cache?.Set(file.FullName, stamp, info);
            return info;
        }

        // Maps an STFS package to a content kind. Content type alone can't separate base from DLC: standalone
        // XBLIG/App titles share 00000002 with DLC addons, so those (and any unknown type) are probed for a
        // top-level default.xex — present => digital base, absent => DLC (or skipped for unknown types).
        private static Xbox360ContentKind? ClassifyStfs(StfsPackage pkg, string path)
        {
            switch (pkg.ContentType)
            {
                case StfsPackage.ContentTypeTitleUpdate:
                    return Xbox360ContentKind.TitleUpdate;
                case StfsPackage.ContentTypeXbla:
                case StfsPackage.ContentTypeGameOnDemand:
                    return Xbox360ContentKind.DigitalBase;
                case StfsPackage.ContentTypeDlc:
                    return pkg.ContainsDefaultXex(path) ? Xbox360ContentKind.DigitalBase : Xbox360ContentKind.Dlc;
                default:
                    return pkg.ContainsDefaultXex(path) ? Xbox360ContentKind.DigitalBase : (Xbox360ContentKind?)null;
            }
        }

        // Reads + caches an STFS container's metadata and classifies it. Title updates (000B0000) and DLC
        // (00000002 without a default.xex) become content units; digital bases (000D0000 / 00007000, or a
        // standalone 00000002 / unknown type that carries a default.xex) become base units. Nulls aren't cached.
        private Xbox360UnitInfo GetStfsUnit(FileSystemInfoBase file)
        {
            var stamp = FileStamp.FromFileSystemInfo(file);
            var cache = _emuLibrary.ScanCache;
            if (cache != null && cache.TryGet<Xbox360UnitInfo>(file.FullName, stamp, out var cached))
                return cached;

            Xbox360UnitInfo info = null;
            try
            {
                if (StfsPackage.TryReadFromFile(file.FullName, out var pkg))
                {
                    var kind = ClassifyStfs(pkg, file.FullName);
                    if (kind.HasValue)
                    {
                        info = new Xbox360UnitInfo()
                        {
                            Path = file.FullName,
                            Kind = kind.Value,
                            TitleId = pkg.TitleId,
                            MediaId = pkg.MediaId,
                            Version = pkg.Version,
                            ContentType = pkg.ContentType,
                            ContentSize = FileLength(file),
                            DisplayName = kind.Value == Xbox360ContentKind.DigitalBase ? pkg.DisplayName : null,
                        };
                        if (kind.Value == Xbox360ContentKind.DigitalBase)
                            info.ThumbnailPath = EnsureThumbnail(info.TitleId, file.FullName);
                        _emuLibrary.Logger.Debug($"[X360] STFS \"{file.FullName}\": type={pkg.ContentType:X8} titleId={pkg.TitleId:X8} mediaId={pkg.MediaId:X8} version={pkg.Version:X8} kind={kind.Value}.");
                    }
                }
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[X360] Failed to read STFS \"{file.FullName}\".");
            }

            if (info != null)
                cache?.Set(file.FullName, stamp, info);
            return info;
        }

        // Extracts a digital base's embedded STFS package thumbnail PNG to a stable per-title-id file under the
        // plugin data dir (shared metadata/x360-thumbs cache, keyed by title id) and returns its path, used as
        // the title's icon. Extract-once / atomic-write / fail-soft is handled by IconCache.
        private string EnsureThumbnail(uint titleId, string stfsPath)
        {
            return IconCache.EnsureIcon(
                _emuLibrary.GetPluginUserDataPath(), "x360-thumbs", titleId.ToString("X8"), "png",
                () => StfsPackage.TryExtractThumbnail(stfsPath, out var png) ? png : null,
                _emuLibrary.Logger);
        }

        // Reads a disc/ZAR base's whole default.xex and extracts its embedded XDBF title image (decrypt +
        // none/basic decompress). Returns the PNG bytes, or null on any failure (incl. an LZX-compressed
        // executable, not yet supported) — fail-soft so the title simply gets no icon. Called only on a cache
        // miss (via IconCache), so the heavy full-executable read happens once per title id.
        private byte[] ExtractDiscTitleImagePng(Xbox360UnitInfo baseUnit)
        {
            try
            {
                byte[] xex = null;
                if (baseUnit.Kind == Xbox360ContentKind.DiscImage)
                {
                    using (var reader = XdvdfsReader.TryOpen(baseUnit.Path))
                        xex = reader?.TryReadDefaultXex(XexFullReadBytes);
                }
                else if (baseUnit.Kind == Xbox360ContentKind.ZarImage)
                {
                    if (!Xbox360ZarReader.TryReadDefaultXex(baseUnit.Path, XexFullReadBytes, out xex))
                        xex = null;
                }

                if (xex == null)
                    return null;
                return XexImage.TryReadTitleImage(xex, baseUnit.TitleId, out var png) ? png : null;
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[X360] Failed to extract title image from \"{baseUnit.Path}\".");
                return null;
            }
        }

        // Runs the per-file parse over `items` through the endpoint-aware scan concurrency governor when one is
        // available, falling back to a sequential pass otherwise (mirrors Ps3Scanner.RunInspections).
        private IReadOnlyList<TResult> RunInspections<TItem, TResult>(
            IReadOnlyList<TItem> items, string endpointPath, Func<TItem, TResult> worker, CancellationToken ct)
        {
            var governor = _emuLibrary.ScanConcurrency;
            if (governor != null)
                return governor.Map(items, endpointPath, worker, ct);

            var results = new List<TResult>(items.Count);
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested)
                    break;
                results.Add(worker(item));
            }
            return results;
        }

        private static string RelativePath(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(root))
                return fullPath;
            var normRoot = root.TrimEnd('\\', '/');
            if (fullPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(normRoot.Length).TrimStart('\\', '/');
            return fullPath;
        }

        private string MaybeMakePortable(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (_emuLibrary.Playnite.ApplicationInfo.IsPortable)
                return path.Replace(_emuLibrary.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
            return path;
        }

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct) =>
            Enumerable.Empty<Game>();

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            // No legacy (pre-protobuf) Xbox 360 GameId format ever existed.
            gameInfo = null;
            return false;
        }
    }
}
