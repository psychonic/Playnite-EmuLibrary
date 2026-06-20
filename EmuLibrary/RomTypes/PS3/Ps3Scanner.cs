using EmuLibrary.PlayniteCommon;
using EmuLibrary.Settings;
using EmuLibrary.Util;
using EmuLibrary.Util.Metadata;
using EmuLibrary.Util.Ps3;
using EmuLibrary.Util.ScanCache;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace EmuLibrary.RomTypes.Ps3
{
    internal class Ps3Scanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;

        public override RomType RomType => RomType.Ps3;
        public override Guid LegacyPluginId => Guid.Parse("7B2E0A0C-3E2A-4F1E-9C2D-2B7E8F4A6D31");

        // PS3 scans by its own format logic (.iso/.pkg/.rap), not the emulator profile's image
        // extensions. RPCS3's built-in Playnite profile declares none, so don't require them.
        public override bool RequiresProfileImageExtensions => false;

        // PS3 title id: 4 letters + 5 digits, e.g. BLES01234, NPUB30001.
        private static readonly Regex TitleIdRegex = new Regex(@"[A-Z]{4}\d{5}", RegexOptions.Compiled);

        public Ps3Scanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        // In-memory composite for a single PS3 title (re-derived at scan and at install time). PS3 is the
        // disc+package family (Family B): updates are non-cumulative (applied all-in-order) and there are
        // license files (RAPs). Item type is Ps3FileInfo.
        internal sealed class Ps3Title : ICompositeContentSet<Ps3FileInfo>
        {
            public string TitleId;
            public string Name;
            public string Version;
            public string SourceFolder; // relative to mapping.SourcePath
            public Ps3BaseKind BaseKind;

            // Uncompressed install footprint: base (iso/folder/extracted-pkg) + updates + DLC.
            public ulong InstallSize;

            // Disc base (BaseKind == Disc): either an .iso (+ optional .dkey) or a folder containing PS3_GAME.
            public string DiscIsoPath;
            public string DiscDkeyPath;
            public string DiscFolderPath;

            // Pkg base (BaseKind == Pkg).
            public string BasePkgPath;

            // SFO scan info for the base (base pkg, or disc SFO); null when no SFO was readable. The base
            // install primitive uses the paths above, not this; it's here to document the composite shape.
            public Ps3FileInfo BaseInfo;

            public List<Ps3FileInfo> Updates = new List<Ps3FileInfo>(); // ascending APP_VER
            public List<Ps3FileInfo> Dlcs = new List<Ps3FileInfo>();
            public List<string> RapPaths = new List<string>();

            Ps3FileInfo ICompositeContentSet<Ps3FileInfo>.Base => BaseInfo;
            IReadOnlyList<Ps3FileInfo> ICompositeContentSet<Ps3FileInfo>.Updates => Updates;
            IReadOnlyList<Ps3FileInfo> ICompositeContentSet<Ps3FileInfo>.Dlc => Dlcs;
            IReadOnlyList<Ps3FileInfo> ICompositeContentSet<Ps3FileInfo>.Licenses =>
                RapPaths.Select(p => new Ps3FileInfo() { FilePath = p, ContentType = Ps3ContentType.Rap }).ToList();
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            if (string.IsNullOrEmpty(mapping.SourcePath) || !Directory.Exists(mapping.SourcePath))
            {
                _emuLibrary.Logger.Warn($"[PS3] Source path \"{mapping.SourcePath}\" is empty or does not exist; nothing to scan for this mapping.");
                yield break;
            }

            var dstPath = mapping.DestinationPathResolved;
            _emuLibrary.Logger.Info($"[PS3] Scanning source \"{mapping.SourcePath}\" (destination \"{dstPath}\").");

            // GameTDB names/metadata (keyed by PS3 disc serial) shadow what's read from PARAM.SFO / folder
            // names. Loaded once, refreshed daily, fail-soft: if unavailable the file-derived values stand.
            var gameTdb = new GameTdb(_emuLibrary, "ps3tdb");

            // Parse each per-title source folder in parallel through the scan concurrency governor (keyed by
            // the source dir). The parse opens PKGs/ISOs — network round-trips that overlap across folders.
            // The PS3 parse path (Ps3Pkg/Ps3Iso/ParamSfo) is stateless across calls and IScanCache is
            // internally locked, so the workers are independent. GameMetadata construction (incl. the on-disk
            // installed-ness checks against the destination) runs single-threaded after the parallel phase.
            var titleDirs = new DirectoryInfo(mapping.SourcePath).EnumerateDirectories().ToList();
            var titles = RunInspections(titleDirs, mapping.SourcePath, titleDir =>
            {
                var t = BuildTitle(mapping, titleDir.FullName, args.CancelToken);
                if (t == null)
                {
                    _emuLibrary.Logger.Warn($"[PS3] No installable base content found in \"{titleDir.FullName}\"; skipping this folder.");
                    return null;
                }

                _emuLibrary.Logger.Debug($"[PS3] Title \"{t.Name}\" ({t.TitleId}) base={t.BaseKind} updates={t.Updates.Count} dlc={t.Dlcs.Count} raps={t.RapPaths.Count} from \"{titleDir.FullName}\".");
                return t;
            }, args.CancelToken);

            foreach (var title in titles)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                if (title == null)
                    continue;

                var gameInfo = new Ps3GameInfo()
                {
                    MappingId = mapping.MappingId,
                    TitleId = title.TitleId,
                    SourceFolder = title.SourceFolder,
                    BaseKind = title.BaseKind,
                };

                bool isInstalled = IsInstalled(mapping, title, dstPath, out var romPath, out var installDir);

                var roms = new List<GameRom>();
                if (isInstalled && !string.IsNullOrEmpty(romPath))
                {
                    var resolvedRom = MaybeMakePortable(romPath);
                    roms.Add(new GameRom(title.Name, resolvedRom));
                }

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

                if (gameTdb.TryGet(title.TitleId, out var meta))
                    meta.ApplyTo(game);

                yield return game;
            }

            // Also surface loose .iso files sitting directly in the source directory (no subfolder per title).
            // Parsed in parallel through the governor like the per-title folders above.
            var looseIsos = new DirectoryInfo(mapping.SourcePath).EnumerateFiles("*.iso").ToList();
            var looseTitles = RunInspections(looseIsos, mapping.SourcePath,
                isoFile => BuildLooseIsoTitle(isoFile.FullName, args.CancelToken), args.CancelToken);

            foreach (var title in looseTitles)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                if (title == null)
                    continue;

                var gameInfo = new Ps3GameInfo()
                {
                    MappingId = mapping.MappingId,
                    TitleId = title.TitleId,
                    SourceFolder = "",
                    SourceIsoFileName = Path.GetFileName(title.DiscIsoPath),
                    BaseKind = Ps3BaseKind.Disc,
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

                if (gameTdb.TryGet(title.TitleId, out var meta))
                    meta.ApplyTo(game);

                yield return game;
            }
        }

        // Runs the per-title parse over `items` through the endpoint-aware scan concurrency governor when one
        // is available (parallel, bounded per-host + global), falling back to a sequential pass otherwise.
        // `worker` is self-contained (own try/catch inside the parse); null results are kept and filtered by
        // the caller.
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

        // Scans one per-title source folder into a composite. Returns null if no base content is found.
        // Shared by the scanner and the install controller (which re-derives at install time).
        internal Ps3Title BuildTitle(EmulatorMapping mapping, string titleDirAbs, CancellationToken ct)
        {
            var titleDir = new DirectoryInfo(titleDirAbs);
            if (!titleDir.Exists)
                return null;

            string discIso = null;
            var dkeysByBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string ps3GameFolder = null;
            Ps3FileInfo basePkg = null;
            var updates = new List<Ps3FileInfo>();
            var dlcs = new List<Ps3FileInfo>();
            var raps = new List<string>();

            // Stamps of every PKG (for the composite per-title size key) and the manifests of any PKGs we
            // open this pass (so the size union reuses them instead of re-opening). Both are scoped to this
            // one title and released when it returns — peak memory is one game's file list, not the library's.
            var pkgStamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
            var freshManifests = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);

            var enumerator = new SafeFileEnumerator(titleDirAbs, "*.*", SearchOption.AllDirectories);
            foreach (var entry in enumerator)
            {
                if (ct.IsCancellationRequested)
                    return null;

                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    if (ps3GameFolder == null && string.Equals(entry.Name, "PS3_GAME", StringComparison.OrdinalIgnoreCase))
                    {
                        // The folder to copy is the one containing PS3_GAME.
                        ps3GameFolder = Path.GetDirectoryName(entry.FullName);
                    }
                    continue;
                }

                var ext = entry.Extension.TrimStart('.').ToLowerInvariant();
                switch (ext)
                {
                    case "iso":
                        // Only a root-level .iso is treated as a disc base.
                        if (discIso == null && string.Equals(Path.GetDirectoryName(entry.FullName), titleDir.FullName, StringComparison.OrdinalIgnoreCase))
                            discIso = entry.FullName;
                        break;

                    case "dkey":
                    case "key":
                        var keyBase = Path.GetFileNameWithoutExtension(entry.Name);
                        if (!dkeysByBase.ContainsKey(keyBase))
                            dkeysByBase[keyBase] = entry.FullName;
                        break;

                    case "rap":
                        raps.Add(entry.FullName);
                        break;

                    case "pkg":
                        pkgStamps[entry.FullName] = FileStamp.FromFileSystemInfo(entry);
                        var info = GetPkgInfo(entry, freshManifests);
                        if (info == null)
                            break;

                        var contentType = ApplyFolderHint(titleDir.FullName, entry.FullName, info.ContentType);
                        _emuLibrary.Logger.Debug($"[PS3] PKG \"{entry.FullName}\": contentId=\"{info.ContentId}\" titleId=\"{info.TitleId}\" category=\"{info.Category}\" appVer=\"{info.AppVer}\" targetAppVer=\"{info.TargetAppVer}\" isPatch={info.IsPatch} classified={info.ContentType}" + (contentType != info.ContentType ? $" (folder hint => {contentType})" : ""));
                        switch (contentType)
                        {
                            case Ps3ContentType.PkgGame:
                                if (basePkg == null)
                                    basePkg = info;
                                else
                                    _emuLibrary.Logger.Warn($"[PS3] Ignoring extra base PKG \"{entry.FullName}\"; base already set to \"{basePkg.FilePath}\".");
                                break;
                            case Ps3ContentType.Update:
                                updates.Add(info);
                                break;
                            case Ps3ContentType.Dlc:
                                dlcs.Add(info);
                                break;
                            default:
                                _emuLibrary.Logger.Warn($"[PS3] Could not classify PKG \"{entry.FullName}\" (CATEGORY=\"{info.Category}\"). Skipping.");
                                break;
                        }
                        break;
                }
            }

            _emuLibrary.Logger.Debug($"[PS3] Folder \"{titleDirAbs}\": discIso={(discIso != null ? "yes" : "no")} ps3GameFolder={(ps3GameFolder != null ? "yes" : "no")} basePkg={(basePkg != null ? "yes" : "no")} pkgsSeen={pkgStamps.Count} updates={updates.Count} dlc={dlcs.Count} raps={raps.Count} dkeys={dkeysByBase.Count}.");

            var title = new Ps3Title()
            {
                SourceFolder = titleDir.Name,
                Updates = CompositeContent.SelectUpdatesToInstall(updates, UpdateInstallStrategy.InstallAllUpdatesInOrder, u => u.AppVerParsed).ToList(),
                Dlcs = dlcs,
                RapPaths = raps,
            };

            // PARAM.SFO read from the disc base (the ISO9660 filesystem + PS3_GAME metadata sit in the
            // disc's unencrypted region, so no disc key is needed). Null when unavailable.
            Ps3FileInfo discInfo = null;

            if (discIso != null)
            {
                title.BaseKind = Ps3BaseKind.Disc;
                title.DiscIsoPath = discIso;
                if (dkeysByBase.TryGetValue(Path.GetFileNameWithoutExtension(discIso), out var dkey))
                    title.DiscDkeyPath = dkey;
                discInfo = GetDiscInfo(discIso, isIso: true);
            }
            else if (ps3GameFolder != null)
            {
                title.BaseKind = Ps3BaseKind.Disc;
                title.DiscFolderPath = ps3GameFolder;
                var folderSfo = Path.Combine(ps3GameFolder, "PS3_GAME", "PARAM.SFO");
                if (File.Exists(folderSfo))
                    discInfo = GetDiscInfo(folderSfo, isIso: false);
            }
            else if (basePkg != null)
            {
                title.BaseKind = Ps3BaseKind.Pkg;
                title.BasePkgPath = basePkg.FilePath;
            }
            else
            {
                // No base content found in this folder.
                return null;
            }

            title.BaseInfo = basePkg ?? discInfo;

            // Title id: from the base pkg / disc SFO, else a token in the iso/folder name, else any
            // update/DLC/RAP title id.
            title.TitleId =
                NullIfEmpty(basePkg?.TitleId)
                ?? NullIfEmpty(discInfo?.TitleId)
                ?? ExtractTitleId(Path.GetFileName(discIso))
                ?? ExtractTitleId(titleDir.Name)
                ?? updates.Select(u => u.TitleId).FirstOrDefault(t => !string.IsNullOrEmpty(t))
                ?? dlcs.Select(d => d.TitleId).FirstOrDefault(t => !string.IsNullOrEmpty(t))
                ?? raps.Select(r => Ps3FileInfo.TitleIdFromContentId(Path.GetFileNameWithoutExtension(r))).FirstOrDefault(t => !string.IsNullOrEmpty(t));

            // Name: SFO TITLE from the base pkg / disc, else a cleaned folder name.
            title.Name =
                NullIfEmpty(basePkg?.Title)
                ?? NullIfEmpty(discInfo?.Title)
                ?? StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(titleDir.Name));

            var latestUpdate = title.Updates.LastOrDefault();
            title.Version = latestUpdate?.AppVer ?? basePkg?.AppVer ?? discInfo?.AppVer;

            // Install footprint. Everything a PKG extracts lands in the same dev_hdd0 game dir, and updates
            // (then DLC) overwrite same-path files from the base / earlier updates (ExtractTo uses
            // FileMode.Create). So the dev_hdd0 portion is the exact UNION of file paths across base pkg +
            // updates (ascending) + DLC, last writer wins. The union is computed from manifests held only
            // transiently (this title) and the summed result is cached per-title, so neither the manifests
            // nor the cache grow with the library. A disc base (iso/folder) is copied to a separate location
            // and is added whole. RAPs are tiny licence blobs and are not counted.
            var hdd0Pkgs = new List<string>();
            if (title.BaseKind == Ps3BaseKind.Pkg && basePkg != null)
                hdd0Pkgs.Add(basePkg.FilePath);
            foreach (var u in title.Updates)
                hdd0Pkgs.Add(u.FilePath);
            foreach (var d in title.Dlcs)
                hdd0Pkgs.Add(d.FilePath);

            ulong hdd0Size = ComputeHdd0SizeBytes(titleDir.FullName, hdd0Pkgs, pkgStamps, freshManifests, ct);

            ulong discBaseSize = 0;
            if (title.DiscIsoPath != null && File.Exists(title.DiscIsoPath))
                discBaseSize = (ulong)new FileInfo(title.DiscIsoPath).Length;
            else if (title.DiscFolderPath != null)
                discBaseSize = GetDirectorySize(title.DiscFolderPath, ct);

            title.InstallSize = discBaseSize + hdd0Size;

            return title;
        }

        // Builds a Ps3Title for a single .iso file loose in the source directory (no per-title subfolder).
        // Looks for a matching .dkey/.key beside the ISO; reads SFO metadata from the ISO filesystem.
        // Returns null if the file no longer exists.
        internal Ps3Title BuildLooseIsoTitle(string isoAbsPath, CancellationToken ct)
        {
            var isoFile = new FileInfo(isoAbsPath);
            if (!isoFile.Exists)
                return null;

            string dkeyPath = null;
            var isoBase = Path.GetFileNameWithoutExtension(isoFile.Name);
            foreach (var ext in new[] { "dkey", "key" })
            {
                var candidate = Path.Combine(isoFile.DirectoryName, isoBase + "." + ext);
                if (File.Exists(candidate))
                {
                    dkeyPath = candidate;
                    break;
                }
            }

            var discInfo = GetDiscInfo(isoAbsPath, isIso: true);

            var title = new Ps3Title()
            {
                SourceFolder = "",
                BaseKind = Ps3BaseKind.Disc,
                DiscIsoPath = isoAbsPath,
                DiscDkeyPath = dkeyPath,
            };

            title.TitleId =
                NullIfEmpty(discInfo?.TitleId)
                ?? ExtractTitleId(isoFile.Name);

            title.Name =
                NullIfEmpty(discInfo?.Title)
                ?? StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(isoFile.Name));

            title.Version = discInfo?.AppVer;
            title.InstallSize = (ulong)isoFile.Length;

            return title;
        }

        // Sum of every file under a directory (recursively). Used for an extracted PS3_GAME disc folder.
        private static ulong GetDirectorySize(string dirAbs, CancellationToken ct)
        {
            ulong total = 0;
            foreach (var entry in new SafeFileEnumerator(dirAbs, "*.*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested)
                    break;
                if (!entry.Attributes.HasFlag(FileAttributes.Directory))
                    total += (ulong)new FileInfo(entry.FullName).Length;
            }
            return total;
        }

        private bool IsInstalled(EmulatorMapping mapping, Ps3Title title, string dstPath, out string romPath, out string installDir)
        {
            romPath = null;
            installDir = null;

            if (title.BaseKind == Ps3BaseKind.Disc)
            {
                if (string.IsNullOrEmpty(dstPath))
                    return false;

                if (title.DiscIsoPath != null)
                {
                    var installedIso = Path.Combine(dstPath, Path.GetFileName(title.DiscIsoPath));
                    installDir = dstPath;
                    romPath = installedIso;
                    return File.Exists(installedIso);
                }

                if (title.DiscFolderPath != null)
                {
                    var folderName = new DirectoryInfo(title.DiscFolderPath).Name;
                    var installedFolder = Path.Combine(dstPath, folderName);
                    installDir = installedFolder;
                    romPath = Path.Combine(installedFolder, "PS3_GAME", "USRDIR", "EBOOT.BIN");
                    return Directory.Exists(installedFolder);
                }

                return false;
            }

            // Pkg base: installed when its game dir exists under dev_hdd0.
            var gameDir = Rpcs3Emulator.GetGameDir(mapping, title.TitleId);
            if (string.IsNullOrEmpty(gameDir))
                return false;

            installDir = gameDir;
            romPath = Path.Combine(gameDir, "USRDIR", "EBOOT.BIN");
            return Directory.Exists(gameDir);
        }

        // Reads + caches PARAM.SFO metadata for a disc base (keyed by file stamp; nulls are not cached).
        // For an .iso it parses the ISO9660 filesystem — PARAM.SFO lives in the disc's unencrypted region,
        // so no disc key is needed; for an extracted PS3_GAME folder it reads the SFO file directly. Returns
        // null when no SFO is available (e.g. its bytes fell in an encrypted region) so the caller falls back
        // to the iso/folder-name heuristics.
        private Ps3FileInfo GetDiscInfo(string sfoSourcePath, bool isIso)
        {
            if (!FileStamp.TryCapture(sfoSourcePath, out var stamp))
                return null;

            var cache = _emuLibrary.ScanCache;
            if (cache != null && cache.TryGet<Ps3FileInfo>(sfoSourcePath, stamp, out var cached))
                return cached;

            Ps3FileInfo info = null;
            try
            {
                ParamSfo sfo;
                bool ok = isIso
                    ? Ps3Iso.TryReadParamSfo(sfoSourcePath, out sfo)
                    : ParamSfo.TryParse(File.ReadAllBytes(sfoSourcePath), out sfo);

                if (ok && sfo != null)
                {
                    info = new Ps3FileInfo()
                    {
                        FilePath = sfoSourcePath,
                        ContentType = Ps3ContentType.DiscBase,
                        TitleId = sfo.TitleId,
                        Title = sfo.Title,
                        AppVer = sfo.AppVer,
                        Category = sfo.Category,
                    };
                }
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[PS3] Failed to read disc SFO from \"{sfoSourcePath}\".");
            }

            if (info != null)
                cache?.Set(sfoSourcePath, stamp, info);

            return info;
        }

        // Reads + caches a PKG's scan metadata (keyed by file stamp; nulls are not cached). When the PKG is
        // actually opened (cache miss) and a freshManifests sink is provided, its file manifest is captured
        // in the same pass so the title's install-size union can use it without re-opening the PKG.
        private Ps3FileInfo GetPkgInfo(FileSystemInfoBase file, IDictionary<string, Dictionary<string, long>> freshManifests)
        {
            var stamp = FileStamp.FromFileSystemInfo(file);
            var cache = _emuLibrary.ScanCache;
            if (cache != null && cache.TryGet<Ps3FileInfo>(file.FullName, stamp, out var cached))
                return cached;

            Ps3FileInfo info = null;
            try
            {
                using (var pkg = Ps3Pkg.Open(file.FullName))
                {
                    var sfo = pkg.ReadParamSfo();
                    info = new Ps3FileInfo()
                    {
                        FilePath = file.FullName,
                        ContentId = pkg.ContentId,
                        TitleId = !string.IsNullOrEmpty(sfo?.TitleId) ? sfo.TitleId : pkg.TitleId,
                        Title = sfo?.Title,
                        AppVer = sfo?.AppVer,
                        TargetAppVer = sfo?.TargetAppVer,
                        Category = sfo?.Category,
                        IsPatch = pkg.IsPatch,
                        ContentType = Ps3FileInfo.Classify(pkg.IsPatch, sfo?.Category, sfo?.AppVer, sfo?.TargetAppVer),
                    };

                    if (freshManifests != null)
                        freshManifests[file.FullName] = ReadManifest(pkg);
                }
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[PS3] Failed to read PKG \"{file.FullName}\". Skipping.");
            }

            if (info != null)
                cache?.Set(file.FullName, stamp, info);

            return info;
        }

        private static Dictionary<string, long> ReadManifest(Ps3Pkg pkg)
        {
            var manifest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in pkg.GetFileEntries())
                manifest[kv.Key] = kv.Value;
            return manifest;
        }

        // Exact dev_hdd0 footprint for a title: the size of the union of file paths across its PKGs (in
        // install order, last writer wins). Cached per-title under a composite stamp over those PKGs, so an
        // unchanged title is free; any added/removed/modified PKG changes the stamp and forces a recompute.
        // On a recompute, manifests opened this pass (freshManifests) are reused; only PKGs that hit the
        // per-file cache (so weren't opened) are read here, and that only happens when the title changed.
        private ulong ComputeHdd0SizeBytes(string titleDirAbs, IReadOnlyList<string> orderedPkgPaths,
            IReadOnlyDictionary<string, FileStamp> pkgStamps,
            IReadOnlyDictionary<string, Dictionary<string, long>> freshManifests, CancellationToken ct)
        {
            if (orderedPkgPaths.Count == 0)
                return 0;

            // Composite stamp: order-independent so it depends only on the set of PKGs, not enumeration order.
            long sizeAcc = 0, mixAcc = 0;
            foreach (var p in orderedPkgPaths)
            {
                if (!pkgStamps.TryGetValue(p, out var s))
                    continue;
                sizeAcc += s.SizeBytes;
                mixAcc ^= unchecked((s.SizeBytes * 397) ^ s.ModifiedUtcTicks ^ StableHash(p));
            }
            var composite = new FileStamp(sizeAcc, mixAcc);

            var cache = _emuLibrary.ScanCache;
            var key = titleDirAbs + "\0hdd0size";
            if (cache != null && cache.TryGet<Ps3InstallSize>(key, composite, out var cachedSize))
                return (ulong)cachedSize.Bytes;

            var union = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in orderedPkgPaths)
            {
                ct.ThrowIfCancellationRequested();

                Dictionary<string, long> manifest;
                if (freshManifests == null || !freshManifests.TryGetValue(p, out manifest))
                    manifest = ReadPkgManifestForSize(p);
                if (manifest == null)
                    continue;

                foreach (var kv in manifest)
                    union[kv.Key] = kv.Value;
            }

            long total = 0;
            foreach (var v in union.Values)
                total += v;

            cache?.Set(key, composite, new Ps3InstallSize { Bytes = total });
            return (ulong)total;
        }

        // Reads a PKG's file manifest for the install-size union. Used only for a PKG whose metadata was
        // served from cache (not opened this pass) while the title's size needed recomputing — uncommon.
        private Dictionary<string, long> ReadPkgManifestForSize(string path)
        {
            try
            {
                using (var pkg = Ps3Pkg.Open(path))
                    return ReadManifest(pkg);
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[PS3] Failed to read PKG manifest \"{path}\" for install size.");
                return null;
            }
        }

        // Stable (run-to-run) hash of a path for the composite stamp. String.GetHashCode isn't guaranteed
        // stable across processes, and this value is persisted in the cache key's stamp, so use FNV-1a over
        // the lowercased path (paths are case-insensitive on Windows).
        private static long StableHash(string s)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL; // FNV-1a 64-bit offset basis
                foreach (var c in s.ToLowerInvariant())
                {
                    h ^= c;
                    h *= 1099511628211UL; // FNV-1a 64-bit prime
                }
                return (long)h;
            }
        }

        // A pkg sitting in an "updates"/"dlc" subfolder is classified by that folder, overriding CATEGORY.
        private static Ps3ContentType ApplyFolderHint(string titleDirAbs, string pkgPathAbs, Ps3ContentType fromCategory)
        {
            var rel = pkgPathAbs.Substring(titleDirAbs.Length).Replace('/', '\\').ToLowerInvariant();
            if (rel.Contains("\\updates\\") || rel.Contains("\\update\\"))
                return Ps3ContentType.Update;
            if (rel.Contains("\\dlc\\") || rel.Contains("\\dlcs\\"))
                return Ps3ContentType.Dlc;
            return fromCategory;
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static string ExtractTitleId(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;
            var m = TitleIdRegex.Match(s.ToUpperInvariant());
            return m.Success ? m.Value : null;
        }

        private string MaybeMakePortable(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (_emuLibrary.Playnite.ApplicationInfo.IsPortable)
                return path.Replace(_emuLibrary.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
            return path;
        }

        // TODO: like Yuzu, detecting uninstalled games whose source files vanished would
        // require scanning the source for base title ids and cross-referencing uninstalled PS3 games.
        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct) =>
            Enumerable.Empty<Game>();

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            // No legacy (pre-protobuf) PS3 GameId format ever existed.
            gameInfo = null;
            return false;
        }
    }
}
