using EmuLibrary.PlayniteCommon;
using EmuLibrary.Settings;
using EmuLibrary.Util;
using EmuLibrary.Util.Metadata;
using EmuLibrary.Util.PlayStation;
using EmuLibrary.Util.ScanCache;
using EmuLibrary.Util.Vita;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace EmuLibrary.RomTypes.Vita
{
    // PS Vita / Vita3K scanner. Handles two source shapes, detected per-container from disk:
    //
    //  * NoNpDrm: pre-decrypted, drop-in trees — base as "app/<TITLEID>/", update as
    //    "patch/<TITLEID>/", joined by title id (the folder name). Install is a plain directory copy. No crypto.
    //
    //  * PSN: NoIntro-named folders holding an encrypted ".pkg" (+ "work.bin" license for base/DLC).
    //    Base/update/DLC are joined by title id (parsed from the pkg filename prefix "XXNNNN-<TITLEID>_00-...").
    //    Install decrypts + extracts each pkg (VitaPkg) into ux0/app|patch|addcont and writes the license .rif
    //    (Zrif). Folder classification: "(DLC)" => DLC, "(Update)"/"-Annnn-" => update, else base.
    //
    // A container is NoNpDrm when it has an "app/<id>" tree, PSN when a folder holds a ".pkg". Installed-ness is
    // always derived from disk each scan (never cached).
    internal class VitaScanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;

        public override RomType RomType => RomType.Vita;

        // No legacy standalone Vita extension to migrate from — a fresh, never-reused GUID.
        public override Guid LegacyPluginId => Guid.Parse("2C3F8B41-9D5E-4A72-BE13-7F0A6C2E8D94");

        public override IReadOnlyCollection<string> SupportedPlatformSpecIds => new[] { "sony_vita" };

        // Vita scans by its own folder logic (title-id folder names), not the emulator profile's image extensions.
        public override bool RequiresProfileImageExtensions => false;

        // A Vita title id: 4 letters + 5 digits (e.g. PCSE00890, PCSB00456). app/patch subfolders are named this.
        private static readonly Regex TitleIdRegex = new Regex(@"^[A-Z]{4}\d{5}$", RegexOptions.Compiled);

        // Title id anywhere in a string (for parsing out of pkg filenames / folder names).
        private static readonly Regex TitleIdAnywhereRegex = new Regex(@"[A-Z]{4}\d{5}", RegexOptions.Compiled);

        // The content-id prefix at the start of a PSN pkg filename, e.g. "UP4395-PCSE00890_00-10SECNINJAVITAUS".
        private static readonly Regex ContentIdFromFileNameRegex =
            new Regex(@"^([A-Za-z0-9]{4,10}-[A-Za-z]{4}\d{5}_\d{2}-[A-Za-z0-9]+)", RegexOptions.Compiled);

        // An update pkg carries an APP_VER token "-Annnn-" (e.g. "-A0101-" = app ver 01.01).
        private static readonly Regex UpdateAppVerRegex = new Regex(@"-A(\d{2})(\d{2})-", RegexOptions.Compiled);

        public VitaScanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        // Serializable param.sfo scan result, cached in IScanCache keyed by the file's stamp (Newtonsoft →
        // public props, parameterless ctor). Reading a tree's sce_sys/param.sfo is a small network round-trip
        // we don't want to repeat for unchanged trees.
        internal sealed class VitaSfoInfo
        {
            public string TitleId { get; set; }
            public string Title { get; set; }
            public string AppVer { get; set; }
            public string Category { get; set; }
        }

        // Identifies one NoNpDrm base title in the source: the container dir that holds its app/patch trees, plus
        // the title id (the app/<id> folder name).
        private sealed class BaseDescriptor
        {
            public string ContainerAbs;
            public string TitleId;
        }

        // One PSN .pkg found in the source (before parsing).
        private sealed class PsnPkgFile
        {
            public string PkgPath;
            public string FolderAbs;
        }

        // A parsed PSN .pkg (filename-derived at scan time; the header content-id is confirmed at extract time).
        private sealed class PsnPkgParse
        {
            public string PkgPath;
            public string FolderAbs;
            public string TitleId;
            public string ContentId;
            public VitaContentKind Kind;
            public string AppVer;
            public Version AppVerParsed = new Version(0, 0);
            public string WorkBinPath;
            public ulong Size;
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            if (string.IsNullOrEmpty(mapping.SourcePath) || !Directory.Exists(mapping.SourcePath))
            {
                _emuLibrary.Logger.Warn($"[Vita] Source path \"{mapping.SourcePath}\" is empty or does not exist; nothing to scan for this mapping.");
                yield break;
            }

            _emuLibrary.Logger.Info($"[Vita] Scanning source \"{mapping.SourcePath}\".");

            var emittedTitleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ---- NoNpDrm: one title per app/<id> tree, parsed in parallel through the scan
            // concurrency governor (reading each tree's sce_sys/param.sfo is a network round-trip). ----
            var bases = EnumerateBases(mapping.SourcePath).ToList();
            var noNpDrmTitles = RunInspections(bases, mapping.SourcePath,
                b => BuildNoNpDrmTitle(mapping, b.ContainerAbs, b.TitleId, args.CancelToken), args.CancelToken);

            foreach (var title in noNpDrmTitles)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;
                if (title == null)
                    continue;

                _emuLibrary.Logger.Debug($"[Vita] NoNpDrm title \"{title.Name}\" ({title.TitleId}) updates={title.Updates.Count} from \"{title.BaseTreeDir}\".");
                emittedTitleIds.Add(title.TitleId);
                yield return BuildGameMetadata(mapping, title);
            }

            // ---- PSN: parse every .pkg found under the source (filename-derived), group by title id,
            // assemble one composite per title. ----
            var psnFiles = EnumeratePsnPkgFiles(mapping.SourcePath);
            if (psnFiles.Count > 0)
            {
                var parses = RunInspections(psnFiles, mapping.SourcePath, ParsePsnPkg, args.CancelToken)
                    .Where(p => p != null && !string.IsNullOrEmpty(p.TitleId))
                    .ToList();

                foreach (var group in parses.GroupBy(p => p.TitleId, StringComparer.OrdinalIgnoreCase))
                {
                    if (args.CancelToken.IsCancellationRequested)
                        yield break;

                    if (emittedTitleIds.Contains(group.Key))
                    {
                        _emuLibrary.Logger.Warn($"[Vita] PSN title id \"{group.Key}\" also present as a NoNpDrm tree; keeping the NoNpDrm entry.");
                        continue;
                    }

                    var title = AssemblePsnTitle(mapping, mapping.SourcePath, group.Key, group.ToList());
                    if (title == null)
                        continue;

                    _emuLibrary.Logger.Debug($"[Vita] PSN title \"{title.Name}\" ({title.TitleId}) updates={title.Updates.Count} dlc={title.DlcList.Count} licenses={title.LicenseList.Count}.");
                    emittedTitleIds.Add(title.TitleId);
                    yield return BuildGameMetadata(mapping, title);
                }
            }
        }

        // Constructs the Playnite GameMetadata for a resolved title (installed-ness from disk + optional icon).
        private GameMetadata BuildGameMetadata(EmulatorMapping mapping, VitaTitle title)
        {
            var gameInfo = new VitaGameInfo()
            {
                MappingId = mapping.MappingId,
                TitleId = title.TitleId,
                SourceFolder = title.SourceFolder,
            };

            bool isInstalled = IsInstalled(mapping, title, out var romPath, out var installDir);

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

            var iconPath = ResolveIconPath(title);
            if (iconPath != null)
                game.Icon = new MetadataFile(iconPath);

            return game;
        }

        // Finds every NoNpDrm base "app/<TITLEID>" tree in the source, handling both layouts without descending
        // into the (potentially large) trees: the candidate containers are SourcePath (Shape 1) plus each of its
        // immediate subdirectories (Shape 2); a container contributes a base for each title-id-named child of its
        // "app" dir.
        private IEnumerable<BaseDescriptor> EnumerateBases(string sourcePath)
        {
            var containers = new List<string> { sourcePath };
            try
            {
                containers.AddRange(new DirectoryInfo(sourcePath).EnumerateDirectories().Select(d => d.FullName));
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[Vita] Failed to enumerate subdirectories of \"{sourcePath}\".");
            }

            foreach (var container in containers)
            {
                var appDir = Path.Combine(container, "app");
                if (!Directory.Exists(appDir))
                    continue;

                IEnumerable<DirectoryInfo> titleDirs;
                try
                {
                    titleDirs = new DirectoryInfo(appDir).EnumerateDirectories().ToList();
                }
                catch (Exception ex)
                {
                    _emuLibrary.Logger.Warn(ex, $"[Vita] Failed to enumerate \"{appDir}\".");
                    continue;
                }

                foreach (var titleDir in titleDirs)
                {
                    if (TitleIdRegex.IsMatch(titleDir.Name))
                        yield return new BaseDescriptor { ContainerAbs = container, TitleId = titleDir.Name };
                }
            }
        }

        // Re-derives one title's composite from the source (used by the scan and by the install/uninstall
        // controllers — the source may have changed since the scan). Detects the shape from disk: an
        // "app/<id>" tree under the container => NoNpDrm; else the PSN pkgs under the container for that title id.
        // Returns null if the title's source content is gone.
        internal VitaTitle BuildTitle(EmulatorMapping mapping, string containerAbs, string titleId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(containerAbs) || string.IsNullOrEmpty(titleId))
                return null;

            var appTreeDir = Path.Combine(containerAbs, "app", titleId);
            if (Directory.Exists(appTreeDir))
                return BuildNoNpDrmTitle(mapping, containerAbs, titleId, ct);

            // PSN: gather this title's pkgs from under the container and assemble.
            var parses = EnumeratePsnPkgFiles(containerAbs)
                .Select(ParsePsnPkg)
                .Where(p => p != null && string.Equals(p.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (parses.Count == 0)
                return null;

            return AssemblePsnTitle(mapping, containerAbs, titleId, parses);
        }

        // Builds a NoNpDrm title: the base "app/<id>" tree + any sibling "patch/<id>" tree.
        private VitaTitle BuildNoNpDrmTitle(EmulatorMapping mapping, string containerAbs, string titleId, CancellationToken ct)
        {
            var appTreeDir = Path.Combine(containerAbs, "app", titleId);
            if (!Directory.Exists(appTreeDir))
                return null;

            var baseSfo = ReadTreeSfo(appTreeDir);
            ulong baseSize = GetDirectorySize(appTreeDir, ct);

            var title = new VitaTitle()
            {
                TitleId = titleId,
                SourceFolder = RelativePath(containerAbs, mapping.SourcePath),
                BaseTreeDir = appTreeDir,
                BaseRef = new VitaContentRef()
                {
                    SourceTreeDir = appTreeDir,
                    TitleId = titleId,
                    Kind = VitaContentKind.App,
                    SourceKind = VitaSourceKind.Tree,
                    ContentSize = baseSize,
                },
            };

            ulong installSize = baseSize;

            // The update is the sibling "patch/<id>" tree in the same container (title id is the join key). The
            // composite supports multiple updates (Family B, all-in-order by APP_VER); a NoNpDrm container holds
            // at most one patch tree per title, so this is a 0-or-1 element list in practice.
            var patchTreeDir = Path.Combine(containerAbs, "patch", titleId);
            if (Directory.Exists(patchTreeDir))
            {
                var patchSfo = ReadTreeSfo(patchTreeDir);
                var patchSize = GetDirectorySize(patchTreeDir, ct);
                var item = new VitaContentRef()
                {
                    SourceTreeDir = patchTreeDir,
                    TitleId = titleId,
                    Kind = VitaContentKind.Patch,
                    SourceKind = VitaSourceKind.Tree,
                    AppVer = NullIfEmpty(patchSfo?.AppVer),
                    AppVerParsed = ParseAppVer(patchSfo?.AppVer),
                    ContentSize = patchSize,
                };
                title.Updates.Add(item);
                installSize += patchSize;
            }

            title.InstallSize = installSize;

            // Name: base tree TITLE, else the per-title container folder name (Shape 2), else the title id.
            title.Name =
                NullIfEmpty(baseSfo?.Title)
                ?? (string.IsNullOrEmpty(title.SourceFolder)
                        ? titleId
                        : StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(new DirectoryInfo(containerAbs).Name)));

            // Version: latest patch APP_VER, else the base APP_VER.
            title.Version = title.Updates.LastOrDefault()?.AppVer ?? NullIfEmpty(baseSfo?.AppVer);

            return title;
        }

        // Assembles a PSN title from the parsed pkgs sharing a title id (one base, ordered updates, DLC,
        // licenses). Returns null if no base pkg is present (orphan updates/DLC are ignored).
        private VitaTitle AssemblePsnTitle(EmulatorMapping mapping, string containerAbs, string titleId, List<PsnPkgParse> parses)
        {
            var basePkg = parses.FirstOrDefault(p => p.Kind == VitaContentKind.App);
            if (basePkg == null)
            {
                _emuLibrary.Logger.Warn($"[Vita] PSN title id \"{titleId}\" has updates/DLC but no base package; skipping.");
                return null;
            }

            var title = new VitaTitle()
            {
                TitleId = titleId,
                SourceFolder = RelativePath(containerAbs, mapping.SourcePath),
                BaseRef = new VitaContentRef()
                {
                    TitleId = titleId,
                    Kind = VitaContentKind.App,
                    SourceKind = VitaSourceKind.Pkg,
                    PkgPath = basePkg.PkgPath,
                    ContentId = basePkg.ContentId,
                    WorkBinPath = basePkg.WorkBinPath,
                    ContentSize = basePkg.Size,
                },
            };

            ulong installSize = basePkg.Size;

            foreach (var upd in parses.Where(p => p.Kind == VitaContentKind.Patch)
                                      .OrderBy(p => p.AppVerParsed))
            {
                title.Updates.Add(new VitaContentRef()
                {
                    TitleId = titleId,
                    Kind = VitaContentKind.Patch,
                    SourceKind = VitaSourceKind.Pkg,
                    PkgPath = upd.PkgPath,
                    ContentId = upd.ContentId,
                    AppVer = upd.AppVer,
                    AppVerParsed = upd.AppVerParsed,
                    ContentSize = upd.Size,
                });
                installSize += upd.Size;
            }

            foreach (var dlc in parses.Where(p => p.Kind == VitaContentKind.AddCont))
            {
                title.DlcList.Add(new VitaContentRef()
                {
                    TitleId = titleId,
                    Kind = VitaContentKind.AddCont,
                    SourceKind = VitaSourceKind.Pkg,
                    PkgPath = dlc.PkgPath,
                    ContentId = dlc.ContentId,
                    WorkBinPath = dlc.WorkBinPath,
                    ContentSize = dlc.Size,
                });
                installSize += dlc.Size;
            }

            // Licenses: the base's work.bin, plus each DLC's work.bin (each named for its own content-id).
            if (!string.IsNullOrEmpty(basePkg.WorkBinPath))
                title.LicenseList.Add(NewLicenseRef(titleId, basePkg.ContentId, basePkg.WorkBinPath));
            foreach (var dlc in parses.Where(p => p.Kind == VitaContentKind.AddCont && !string.IsNullOrEmpty(p.WorkBinPath)))
                title.LicenseList.Add(NewLicenseRef(titleId, dlc.ContentId, dlc.WorkBinPath));

            title.InstallSize = installSize;

            // Name from the base folder name (cleaned); title id fallback. No SFO decrypt at scan time.
            var baseFolderName = new DirectoryInfo(basePkg.FolderAbs).Name;
            title.Name = NullIfEmpty(StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(baseFolderName)))
                ?? titleId;

            title.Version = title.Updates.LastOrDefault()?.AppVer;

            return title;
        }

        private static VitaContentRef NewLicenseRef(string titleId, string contentId, string workBinPath) =>
            new VitaContentRef()
            {
                TitleId = titleId,
                Kind = VitaContentKind.License,
                SourceKind = VitaSourceKind.License,
                ContentId = contentId,
                WorkBinPath = workBinPath,
            };

        // Enumerates every PSN .pkg under the container: a folder that directly contains a ".pkg" is a PSN
        // content/update folder. Searched at depth 1 (flat NoIntro layout) and depth 2 (e.g. a
        // Content/… / Updates/… wrapper). NoNpDrm trees hold no ".pkg", so the two shapes never collide.
        private List<PsnPkgFile> EnumeratePsnPkgFiles(string containerAbs)
        {
            var result = new List<PsnPkgFile>();
            foreach (var d1 in SafeEnumerateDirs(containerAbs))
            {
                var pkgs = SafeEnumeratePkgs(d1);
                if (pkgs.Count > 0)
                {
                    foreach (var p in pkgs)
                        result.Add(new PsnPkgFile { PkgPath = p, FolderAbs = d1 });
                }
                else
                {
                    foreach (var d2 in SafeEnumerateDirs(d1))
                    {
                        foreach (var p in SafeEnumeratePkgs(d2))
                            result.Add(new PsnPkgFile { PkgPath = p, FolderAbs = d2 });
                    }
                }
            }
            return result;
        }

        private List<string> SafeEnumerateDirs(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).EnumerateDirectories().Select(d => d.FullName).ToList();
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[Vita] Failed to enumerate subdirectories of \"{dir}\".");
                return new List<string>();
            }
        }

        private List<string> SafeEnumeratePkgs(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).EnumerateFiles("*.pkg").Select(f => f.FullName).ToList();
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[Vita] Failed to enumerate .pkg files in \"{dir}\".");
                return new List<string>();
            }
        }

        // Parses one PSN .pkg from its filename + folder (no header read — cheap over the network; the header
        // content-id is confirmed at extract time). [VERIFY] the filename prefix vs the header 0x30 content-id
        // against real packages.
        private PsnPkgParse ParsePsnPkg(PsnPkgFile file)
        {
            var fileName = Path.GetFileName(file.PkgPath);
            var contentId = ParseContentIdFromFileName(fileName);
            var titleId = VitaPkg.TitleIdFromContentId(contentId) ?? ExtractTitleId(fileName);
            if (string.IsNullOrEmpty(titleId))
                return null;

            var kind = ClassifyPsnFolder(file.FolderAbs, fileName);

            string appVer = null;
            var appVerParsed = new Version(0, 0);
            if (kind == VitaContentKind.Patch)
                appVerParsed = ParseUpdateAppVer(fileName, out appVer);

            string workBin = null;
            if (kind != VitaContentKind.Patch)
            {
                var wb = Path.Combine(file.FolderAbs, "work.bin");
                if (File.Exists(wb))
                    workBin = wb;
            }

            ulong size = 0;
            try { size = (ulong)new FileInfo(file.PkgPath).Length; }
            catch (Exception ex) { _emuLibrary.Logger.Warn(ex, $"[Vita] Failed to stat \"{file.PkgPath}\"."); }

            return new PsnPkgParse()
            {
                PkgPath = file.PkgPath,
                FolderAbs = file.FolderAbs,
                TitleId = titleId,
                ContentId = contentId,
                Kind = kind,
                AppVer = appVer,
                AppVerParsed = appVerParsed,
                WorkBinPath = workBin,
                Size = size,
            };
        }

        // Classifies a PSN folder by its NoIntro name (and the pkg's app-version token for updates).
        private static VitaContentKind ClassifyPsnFolder(string folderAbs, string pkgFileName)
        {
            var folderName = new DirectoryInfo(folderAbs).Name;
            if (folderName.IndexOf("(DLC)", StringComparison.OrdinalIgnoreCase) >= 0)
                return VitaContentKind.AddCont;
            if (folderName.IndexOf("(Update)", StringComparison.OrdinalIgnoreCase) >= 0
                || folderName.IndexOf("(Patch)", StringComparison.OrdinalIgnoreCase) >= 0
                || UpdateAppVerRegex.IsMatch(pkgFileName))
                return VitaContentKind.Patch;
            return VitaContentKind.App;
        }

        private static string ParseContentIdFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;
            var m = ContentIdFromFileNameRegex.Match(fileName);
            return m.Success ? m.Groups[1].Value : null;
        }

        // Parses "-A0101-" (01.01) from an update pkg filename. Version(0,0)/null when absent.
        private static Version ParseUpdateAppVer(string fileName, out string appVerStr)
        {
            appVerStr = null;
            if (string.IsNullOrEmpty(fileName))
                return new Version(0, 0);
            var m = UpdateAppVerRegex.Match(fileName);
            if (!m.Success)
                return new Version(0, 0);
            appVerStr = $"{m.Groups[1].Value}.{m.Groups[2].Value}";
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }

        // Reads + caches a tree's sce_sys/param.sfo (keyed by file stamp; nulls aren't cached). Never throws.
        private VitaSfoInfo ReadTreeSfo(string treeDir)
        {
            var sfoPath = Path.Combine(treeDir, "sce_sys", "param.sfo");
            if (!FileStamp.TryCapture(sfoPath, out var stamp))
                return null;

            var cache = _emuLibrary.ScanCache;
            if (cache != null && cache.TryGet<VitaSfoInfo>(sfoPath, stamp, out var cached))
                return cached;

            VitaSfoInfo info = null;
            try
            {
                if (ParamSfo.TryParse(File.ReadAllBytes(sfoPath), out var sfo) && sfo != null)
                {
                    info = new VitaSfoInfo()
                    {
                        TitleId = NullIfEmpty(sfo.TitleId),
                        Title = NullIfEmpty(sfo.Title),
                        AppVer = NullIfEmpty(sfo.AppVer),
                        Category = NullIfEmpty(sfo.Category),
                    };
                }
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[Vita] Failed to read param.sfo at \"{sfoPath}\".");
            }

            if (info != null)
                cache?.Set(sfoPath, stamp, info);
            return info;
        }

        // Installed iff the base app tree exists under the resolved ux0 (Ps3/Psp model — installed state derived
        // from disk, never cached). Rom path = ux0/app/<id>/eboot.bin.
        private bool IsInstalled(EmulatorMapping mapping, VitaTitle title, out string romPath, out string installDir)
        {
            romPath = null;
            installDir = null;

            var appDir = VitaEmulator.GetAppDir(mapping, title.TitleId);
            if (string.IsNullOrEmpty(appDir))
                return false;

            installDir = appDir;
            romPath = Path.Combine(appDir, "eboot.bin");
            return Directory.Exists(appDir);
        }

        // Resolves the base's sce_sys/icon0.png into the shared per-title-id icon cache
        // (metadata/vita-icons/<titleId>.png), fail-soft -> null. NoNpDrm reads the loose file from the extracted
        // app tree; PSN extracts just that one entry from the encrypted base pkg (paid once — EnsureIcon only
        // invokes the producer on a cache miss). The result is cached by title id so the pkg touch isn't repeated.
        private string ResolveIconPath(VitaTitle title)
        {
            if (string.IsNullOrEmpty(title.TitleId))
                return null;

            // NoNpDrm: icon0.png is a plain file in the extracted app tree — no crypto.
            if (!string.IsNullOrEmpty(title.BaseTreeDir))
            {
                return IconCache.EnsureIcon(
                    _emuLibrary.GetPluginUserDataPath(), "vita-icons", title.TitleId, "png",
                    () =>
                    {
                        var iconFile = Path.Combine(title.BaseTreeDir, "sce_sys", "icon0.png");
                        if (!File.Exists(iconFile))
                            return null;
                        var bytes = File.ReadAllBytes(iconFile);
                        return Png.HasPngSignature(bytes) ? bytes : null;
                    },
                    _emuLibrary.Logger);
            }

            // PSN: icon0.png lives inside the encrypted base pkg — extract just that entry (reads the item table
            // near the file's front plus the small icon region, not the whole package).
            var baseRef = title.BaseRef;
            if (baseRef != null && baseRef.SourceKind == VitaSourceKind.Pkg && !string.IsNullOrEmpty(baseRef.PkgPath))
            {
                return IconCache.EnsureIcon(
                    _emuLibrary.GetPluginUserDataPath(), "vita-icons", title.TitleId, "png",
                    () =>
                    {
                        try
                        {
                            // icon0.png is a PFS-encrypted file, so pass the base license's klicensee to also
                            // strip the inner layer (otherwise the bytes are PFS ciphertext, not a PNG).
                            var klic = KlicenseeFromWorkBin(baseRef.WorkBinPath);
                            using (var pkg = VitaPkg.Open(baseRef.PkgPath, klic))
                            {
                                if (pkg.TryExtractItem("sce_sys/icon0.png", out var bytes)
                                    && bytes != null && Png.HasPngSignature(bytes))
                                    return bytes;
                            }
                        }
                        catch (Exception ex)
                        {
                            _emuLibrary.Logger.Warn(ex, $"[Vita] Failed to extract icon from \"{baseRef.PkgPath}\".");
                        }
                        return null;
                    },
                    _emuLibrary.Logger);
            }

            return null;
        }

        // APP_VER is "NN.NN" (e.g. "01.02"). Compare numerically so "01.10" > "01.02". This is the ONLY thing
        // that orders updates — never the folder name.
        // Decodes the inner PFS klicensee from a title's work.bin/zRIF; null when absent/unreadable.
        private static byte[] KlicenseeFromWorkBin(string workBinPath)
        {
            if (string.IsNullOrEmpty(workBinPath) || !System.IO.File.Exists(workBinPath))
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

        private static Version ParseAppVer(string appVer)
        {
            if (!string.IsNullOrWhiteSpace(appVer) && Version.TryParse(appVer.Trim(), out var v))
                return v;
            return new Version(0, 0);
        }

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

        // Runs the per-title parse over `items` through the endpoint-aware scan concurrency governor when one is
        // available, falling back to a sequential pass otherwise (mirrors Ps3Scanner/PspScanner).
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

        private static string ExtractTitleId(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;
            var m = TitleIdAnywhereRegex.Match(s.ToUpperInvariant());
            return m.Success ? m.Value : null;
        }

        private static string RelativePath(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(root))
                return fullPath;
            var normRoot = root.TrimEnd('\\', '/');
            if (string.Equals(fullPath.TrimEnd('\\', '/'), normRoot, StringComparison.OrdinalIgnoreCase))
                return "";
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

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct) =>
            Enumerable.Empty<Game>();

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            // No legacy (pre-protobuf) Vita GameId format ever existed.
            gameInfo = null;
            return false;
        }
    }
}
