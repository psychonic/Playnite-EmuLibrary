using EmuLibrary.PlayniteCommon;
using EmuLibrary.RomTypes.ThreeDs.Crypto;
using EmuLibrary.Util;
using EmuLibrary.Util.ScanCache;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.ThreeDs
{
    // Emulator-facing helper for Azahar / 3DS content-store titles (CIA and CDN folders).
    // Resolves Azahar's data paths and keys, enumerates .cia units from a source folder, groups them
    // into composites (base + latest update + DLC) by base title id, derives installed-state from the SD store,
    // and produces the SD installer. Loose bootable files are handled directly by the scanner, not here.
    internal sealed class Azahar
    {
        // Per-unit locator info (a .cia file or a CDN folder), cached (JSON) by the source's stamp. SourceKind
        // and TicketPath are re-applied after a cache hit rather than trusted from the cached blob (a .tik can be
        // added beside a folder after the tmd was first cached, and an older cache predates SourceKind).
        public sealed class UnitInfo
        {
            public string Path { get; set; }
            public ulong BaseTitleId { get; set; }
            public ulong TitleId { get; set; }
            public ThreeDsContentKind Kind { get; set; }
            public uint Version { get; set; }
            public ulong ContentSize { get; set; }
            public ThreeDsSourceKind SourceKind { get; set; }
            public string TicketPath { get; set; }
        }

        private readonly string _emulatorBaseDir;
        private readonly ILogger _logger;
        private readonly IScanCache _scanCache;
        private AzaharPaths _paths;
        private ThreeDsKeys _cachedKeys;
        private bool _keysLoaded;

        public Azahar(string emulatorBaseDir, ILogger logger, IScanCache scanCache = null)
        {
            _emulatorBaseDir = emulatorBaseDir;
            _logger = logger;
            _scanCache = scanCache;
        }

        public AzaharPaths Paths => _paths ?? (_paths = AzaharPaths.Resolve(_emulatorBaseDir));

        // Key file search order: Azahar's sysdata, then the source folder, then a "3DS Keys" subfolder of the
        // source (matching how the share organizes them). The built-in keyblob is the final fallback (handled
        // inside ThreeDsKeys.Load using the emulator base dir).
        public ThreeDsKeys LoadKeys(string sourcePath)
        {
            var candidates = new List<string> { Paths.SysDataKeysPath };
            if (!string.IsNullOrEmpty(sourcePath))
            {
                candidates.Add(System.IO.Path.Combine(sourcePath, "aes_keys.txt"));
                candidates.Add(System.IO.Path.Combine(sourcePath, "3DS Keys", "aes_keys.txt"));
            }
            return ThreeDsKeys.Load(candidates, _emulatorBaseDir, _logger);
        }

        public SdTitleInstaller CreateInstaller(string sourcePath) =>
            new SdTitleInstaller(Paths, LoadKeys(sourcePath), _logger);

        // Loads and caches the keys for this scan, so reading the SMDH of many uninstalled titles derives the
        // Azahar keyblob only once.
        private ThreeDsKeys KeysFor(string sourcePath)
        {
            if (!_keysLoaded)
            {
                // Fail-soft (and cache the failure): a no_crypto dump / SD .app needs no keys, so a missing
                // keyset must not block reading its SMDH — only an encrypted title is then unreadable. Caching
                // null also avoids re-attempting the (potentially expensive) keyblob extraction per file.
                try
                {
                    _cachedKeys = LoadKeys(sourcePath);
                }
                catch (Exception ex)
                {
                    _logger?.Info($"[3DS] No 3DS keys available ({ex.Message}); encrypted titles won't yield metadata this scan.");
                    _cachedKeys = null;
                }
                _keysLoaded = true;
            }
            return _cachedKeys;
        }

        // Reads a title's SMDH (authoritative title + 48x48 icon). An installed title's boot .app is already a
        // decrypted no_crypto NCCH, so it is read with no keys. Otherwise a bounded decrypt of the source base
        // content reads just the SMDH; the result is cached by the base file's stamp because the source is
        // typically an SMB share and re-reading it every scan would be costly. Returns null when none is
        // available (loose base, missing ticket/keys, not an application). Fail-soft.
        public Smdh.Info TryReadSmdh(ThreeDsTitle title, string sourcePath, string installedBootApp, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(installedBootApp))
            {
                var info = Smdh.TryReadFromNcch(installedBootApp);
                if (info != null)
                    return info;
            }

            var baseRef = title?.BaseRef;
            if (baseRef == null || title.BaseLicenseMissing)
                return null; // loose bases are handled by the scanner; a missing ticket can't decrypt
            if (baseRef.SourceKind != ThreeDsSourceKind.Cia && baseRef.SourceKind != ThreeDsSourceKind.CdnFolder)
                return null;

            var cacheKey = "smdh:" + baseRef.SourcePath;
            var stampFile = baseRef.SourceKind == ThreeDsSourceKind.CdnFolder
                ? CdnTitle.FindTmd(baseRef.SourcePath)
                : baseRef.SourcePath;

            FileStamp stamp = default(FileStamp);
            bool haveStamp = stampFile != null && FileStamp.TryCapture(stampFile, out stamp);
            if (haveStamp && _scanCache != null && _scanCache.TryGet<Smdh.Info>(cacheKey, stamp, out var cached))
                return cached;

            Smdh.Info result = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                var keys = KeysFor(sourcePath);

                // A .zcia would otherwise be fully decompressed to a temp .cia by Cia.Open just to read its icon;
                // read it from a bounded front prefix instead (the icon precedes the multi-GB RomFS). Plain .cia
                // and CDN folders are already cheap random-access reads.
                if (baseRef.SourceKind == ThreeDsSourceKind.Cia && Z3ds.IsZ3ds(baseRef.SourcePath))
                {
                    result = Cia.TryReadSmdhFromZ3ds(baseRef.SourcePath, keys, ct);
                }
                else
                {
                    using (var dt = SdTitleInstaller.OpenTitle(baseRef))
                        result = Smdh.TryReadFromEncryptedTitle(dt, keys);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[3DS] Failed to read SMDH for title {title.TitleId:X16}.");
            }

            if (result != null && haveStamp)
                _scanCache?.Set(cacheKey, stamp, result);
            return result;
        }

        // Reads the title id + SMDH (name + 48x48 icon) of a loose 3DS image (.cci/.3ds/.cxi or a Z3DS variant),
        // cached by the file's stamp because the source is typically an SMB share and re-reading it every scan
        // would be costly. Keys are loaded once per scan (KeysFor) so an encrypted dump can be decrypted; a
        // no_crypto dump needs none. Returns null when nothing usable is present. Fail-soft.
        public ThreeDsLooseInfo.Result TryReadLooseInfo(string looseFilePath, string sourcePathForKeys, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(looseFilePath) || !File.Exists(looseFilePath))
                return null;

            var cacheKey = "smdh-loose:" + looseFilePath;
            bool haveStamp = FileStamp.TryCapture(looseFilePath, out var stamp);
            if (haveStamp && _scanCache != null && _scanCache.TryGet<ThreeDsLooseInfo.Result>(cacheKey, stamp, out var cached))
                return cached;

            ct.ThrowIfCancellationRequested();
            var result = ThreeDsLooseInfo.TryRead(looseFilePath, KeysFor(sourcePathForKeys), _logger, ct);

            if (result != null && haveStamp)
                _scanCache?.Set(cacheKey, stamp, result);
            return result;
        }

        public bool IsTitleInstalled(ulong baseTitleId) => Paths.IsTitleInstalled(baseTitleId);

        #region Source enumeration / grouping

        public IEnumerable<ThreeDsTitle> GetTitlesFromDir(string sourcePath, CancellationToken ct)
        {
            var units = EnumerateUnits(sourcePath, ct).ToList();

            var byBase = new Dictionary<ulong, List<UnitInfo>>();
            foreach (var u in units)
            {
                if (u.Kind == ThreeDsContentKind.Other)
                    continue; // not application content
                if (!byBase.TryGetValue(u.BaseTitleId, out var list))
                    byBase[u.BaseTitleId] = list = new List<UnitInfo>();
                list.Add(u);
            }

            foreach (var kv in byBase)
            {
                ct.ThrowIfCancellationRequested();

                var title = BuildTitleFromGroup(kv.Key, kv.Value);
                if (title != null)
                    yield return title;
            }
        }

        // Re-derives one title by base id from the source (used by the install controller).
        public ThreeDsTitle BuildTitle(string sourcePath, ulong baseTitleId, CancellationToken ct)
        {
            var group = EnumerateUnits(sourcePath, ct)
                .Where(u => u.BaseTitleId == baseTitleId && u.Kind != ThreeDsContentKind.Other)
                .ToList();
            return group.Count == 0 ? null : BuildTitleFromGroup(baseTitleId, group);
        }

        private ThreeDsTitle BuildTitleFromGroup(ulong baseTitleId, List<UnitInfo> group)
        {
            var baseUnit = group.FirstOrDefault(x => x.Kind == ThreeDsContentKind.Application)
                ?? group.FirstOrDefault(x => x.Kind == ThreeDsContentKind.Demo);
            if (baseUnit == null)
                return null; // updates/DLC with no base in the source — nothing to install

            var update = CompositeContent.SelectUpdatesToInstall(
                group.Where(x => x.Kind == ThreeDsContentKind.Update),
                UpdateInstallStrategy.InstallLatestUpdateOnly,
                x => x.Version).FirstOrDefault();

            var dlc = group.Where(x => x.Kind == ThreeDsContentKind.Dlc)
                .GroupBy(x => x.TitleId).Select(g => g.First()).ToList();

            // A CDN unit needs an external .tik to decrypt; CIA carries its own. The base ticket being absent is
            // blocking (flagged like a PS3 missing-RAP). An update/DLC without its own .tik can't be installed
            // either, but isn't blocking — drop it with a warning so the base still installs.
            bool baseLicenseMissing = NeedsTicket(baseUnit);
            if (update != null && NeedsTicket(update))
            {
                _logger?.Warn($"[3DS] Update for {baseTitleId:X16} has no .tik in \"{update.Path}\"; skipping it.");
                update = null;
            }
            dlc = dlc.Where(d =>
            {
                if (!NeedsTicket(d))
                    return true;
                _logger?.Warn($"[3DS] DLC {d.TitleId:X16} has no .tik in \"{d.Path}\"; skipping it.");
                return false;
            }).ToList();

            var name = StringExtensions.NormalizeGameName(
                StringExtensions.GetPathWithoutAllExtensions(System.IO.Path.GetFileName(baseUnit.Path)));

            return new ThreeDsTitle
            {
                TitleId = baseTitleId,
                Name = name,
                BaseKind = ThreeDsBaseKind.ContentStore,
                Version = update?.Version ?? baseUnit.Version,
                InstallSize = baseUnit.ContentSize + (update?.ContentSize ?? 0) + (ulong)dlc.Sum(d => (decimal)d.ContentSize),
                BaseLicenseMissing = baseLicenseMissing,
                BaseRef = ToRef(baseUnit),
                UpdateRef = update != null ? ToRef(update) : null,
                DlcRefs = dlc.Select(ToRef).ToList(),
            };
        }

        // A CDN unit is decryptable only with its external ticket; a missing .tik makes it uninstallable.
        private static bool NeedsTicket(UnitInfo u) =>
            u.SourceKind == ThreeDsSourceKind.CdnFolder && string.IsNullOrEmpty(u.TicketPath);

        // All content-store units under the source: loose .cia files plus CDN folders (tmd + content files).
        public IEnumerable<UnitInfo> EnumerateUnits(string sourcePath, CancellationToken ct) =>
            EnumerateCiaUnits(sourcePath, ct).Concat(EnumerateCdnUnits(sourcePath, ct));

        // Both plain .cia and Z3DS-compressed .zcia are single-title packages; Cia.Open transparently
        // decompresses the latter, so they enumerate identically here.
        private static readonly HashSet<string> s_ciaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cia", ".zcia",
        };

        public IEnumerable<UnitInfo> EnumerateCiaUnits(string sourcePath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
                yield break;

            foreach (var entry in new SafeFileEnumerator(sourcePath, "*", SearchOption.TopDirectoryOnly))
            {
                if (ct.IsCancellationRequested)
                    yield break;
                if (entry.Attributes.HasFlag(FileAttributes.Directory) || !s_ciaExtensions.Contains(Path.GetExtension(entry.Name)))
                    continue;

                var info = GetCiaUnit(entry.FullName, ct);
                if (info != null)
                    yield return info;
            }
        }

        // Each immediate subfolder holding a tmd is one CDN title (base / update / DLC / demo are sibling
        // folders that group by base title id). Folders missing any content (e.g. an interrupted download) are
        // skipped; a missing .tik isn't skipped — it scans and is flagged BaseLicenseMissing at grouping.
        public IEnumerable<UnitInfo> EnumerateCdnUnits(string sourcePath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
                yield break;

            foreach (var dir in new SafeFileEnumerator(sourcePath, "*", SearchOption.TopDirectoryOnly))
            {
                if (ct.IsCancellationRequested)
                    yield break;
                if (!dir.Attributes.HasFlag(FileAttributes.Directory))
                    continue;

                var info = GetCdnUnit(dir.FullName);
                if (info != null)
                    yield return info;
            }
        }

        private UnitInfo GetCiaUnit(string ciaPath, CancellationToken ct)
        {
            UnitInfo info = GetOrParseUnit(ciaPath, ciaPath, ThreeDsSourceKind.Cia,
                () => FromTmd(ciaPath, ReadCiaTmd(ciaPath, ct)),
                $"[3DS] Failed to read CIA \"{ciaPath}\".");

            if (info != null)
            {
                info.SourceKind = ThreeDsSourceKind.Cia;
                info.TicketPath = null; // a CIA carries its own ticket
            }
            return info;
        }

        // Reads only the tmd for the scan path. For a .zcia this parses a small decompressed prefix instead of
        // materializing the whole .cia; a malformed/oversized header falls back to a full decode. A plain .cia is
        // a cheap seek (no decompress). The install path always uses the full Cia.Open and is unaffected.
        private ThreeDsTitleMetadata ReadCiaTmd(string ciaPath, CancellationToken ct)
        {
            if (Z3ds.IsZ3ds(ciaPath))
            {
                try
                {
                    return Cia.ReadMetadataFromZ3ds(ciaPath, ct).Tmd;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Info($"[3DS] Prefix tmd parse failed for \"{ciaPath}\" ({ex.Message}); decoding in full.");
                }
            }
            using (var cia = Cia.Open(ciaPath))
                return cia.Tmd;
        }

        private UnitInfo GetCdnUnit(string folder)
        {
            var tmdPath = CdnTitle.FindTmd(folder);
            if (tmdPath == null)
                return null; // not a CDN title folder

            UnitInfo info = GetOrParseUnit(folder, tmdPath, ThreeDsSourceKind.CdnFolder, () =>
            {
                var tmd = ThreeDsTitleMetadata.Parse(File.ReadAllBytes(tmdPath));
                if (!CdnTitle.IsComplete(folder, tmd))
                {
                    _logger?.Info($"[3DS] CDN folder \"{folder}\" is missing content files; skipping.");
                    return null;
                }
                return FromTmd(folder, tmd);
            }, $"[3DS] Failed to read CDN folder \"{folder}\".");

            if (info != null)
            {
                info.SourceKind = ThreeDsSourceKind.CdnFolder;
                info.TicketPath = ResolveCdnTicket(folder);
            }
            return info;
        }

        // Shared cache+parse wrapper: cache keyed by the stamp of the unit's defining file (the .cia or the tmd).
        private UnitInfo GetOrParseUnit(string path, string stampFile, ThreeDsSourceKind kind, Func<UnitInfo> parse, string failMessage)
        {
            bool haveStamp = FileStamp.TryCapture(stampFile, out var stamp);
            if (haveStamp && _scanCache != null && _scanCache.TryGet<UnitInfo>(path, stamp, out var cached))
                return cached;

            try
            {
                var info = parse();
                if (info != null && haveStamp)
                    _scanCache?.Set(path, stamp, info);
                return info;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, failMessage);
                return null;
            }
        }

        private static UnitInfo FromTmd(string path, ThreeDsTitleMetadata tmd) => new UnitInfo
        {
            Path = path,
            BaseTitleId = tmd.BaseTitleId,
            TitleId = tmd.TitleId,
            Kind = tmd.Kind,
            Version = tmd.TitleVersion,
            ContentSize = tmd.TotalContentSize,
        };

        // The ticket for a CDN folder: a "<folder>.tik" sibling first (how the share names them), else any
        // *.tik inside the folder. Returns null if none is present.
        private static string ResolveCdnTicket(string folder)
        {
            var beside = folder + ".tik";
            if (File.Exists(beside))
                return beside;

            try
            {
                return Directory.EnumerateFiles(folder, "*.tik", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static ThreeDsContentRef ToRef(UnitInfo u) => new ThreeDsContentRef
        {
            SourcePath = u.Path,
            SourceKind = u.SourceKind,
            TitleId = u.TitleId,
            Version = u.Version,
            TicketPath = u.TicketPath,
            ContentSize = u.ContentSize,
        };

        #endregion

        // For an installed content-store title, resolve the bootable .app from the SD-stored tmd (its content[0]
        // id) and the on-disk install size. Returns false if the title is not installed / incomplete.
        public bool TryGetInstalledRom(ulong baseTitleId, out string romPath, out ulong installSize)
        {
            romPath = null;
            installSize = 0;

            var contentDir = Paths.ContentDir(baseTitleId);
            var tmdPath = System.IO.Path.Combine(contentDir, "00000000.tmd");
            if (!File.Exists(tmdPath))
                return false;

            try
            {
                var tmd = ThreeDsTitleMetadata.Parse(File.ReadAllBytes(tmdPath));
                var boot = tmd.Contents.OrderBy(c => c.Index).FirstOrDefault();
                if (boot == null)
                    return false;

                var app = System.IO.Path.Combine(contentDir, $"{boot.Id:x8}.app");
                if (!File.Exists(app))
                    return false;

                romPath = app;
                installSize = DirectorySize(Paths.TitleDir(baseTitleId));
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[3DS] Failed to read installed tmd for title {baseTitleId:X16}.");
                return false;
            }
        }

        private static ulong DirectorySize(string dir)
        {
            try
            {
                ulong total = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    total += (ulong)new FileInfo(f).Length;
                return total;
            }
            catch { return 0; }
        }
    }
}
