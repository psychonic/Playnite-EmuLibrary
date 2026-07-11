using EmuLibrary.PlayniteCommon;
using EmuLibrary.Settings;
using EmuLibrary.Util;
using EmuLibrary.Util.Metadata;
using EmuLibrary.Util.Ps3;
using EmuLibrary.Util.Psp;
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

namespace EmuLibrary.RomTypes.Psp
{
    // PSP / PPSSPP scanner. The source is a flat directory of disc images (freely named .chd/.iso/.cso/.zso/.pbp)
    // beside optional update/DLC payload folders each named by a bare title id. Association is by the DISC_ID read
    // from the image (cached), matched to a sibling folder of that name; the base is copied to DestinationPath and
    // the payload folder is copied verbatim into the emulator's memory stick under PSP/GAME/<titleId>.
    internal class PspScanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;

        public override RomType RomType => RomType.Psp;

        // No legacy standalone PSP extension to migrate from.
        public override Guid LegacyPluginId => Guid.Parse("D3B7F1A2-6C48-4E9D-8A5B-1F0E7C2D9A63");

        // PSP scans by its own format logic (image title-id read), not the emulator profile's image extensions.
        public override bool RequiresProfileImageExtensions => false;

        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chd", "iso", "cso", "zso", "pbp",
        };

        // A bare PSP title id: 4 letters + 5 digits (e.g. ULUS10538, NPUG80318). Payload folders are named this.
        private static readonly Regex TitleIdRegex = new Regex(@"^[A-Z]{4}\d{5}$", RegexOptions.Compiled);

        public PspScanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        // Serializable per-image scan result, cached in IScanCache keyed by the file's stamp (Newtonsoft →
        // public props, parameterless ctor). Reading DISC_ID out of a CHD/CSO/ISO is a network round-trip we
        // don't want to repeat for unchanged images.
        internal sealed class PspImageInfo
        {
            public string Path { get; set; }
            public string TitleId { get; set; }  // DISC_ID / TITLE_ID; null when unreadable
            public string Name { get; set; }     // SFO TITLE; null → caller falls back to the filename
            public string Version { get; set; }  // DISC_VERSION / APP_VER base version; null when unreadable
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            if (string.IsNullOrEmpty(mapping.SourcePath) || !Directory.Exists(mapping.SourcePath))
            {
                _emuLibrary.Logger.Warn($"[PSP] Source path \"{mapping.SourcePath}\" is empty or does not exist; nothing to scan for this mapping.");
                yield break;
            }

            var dstPath = mapping.DestinationPathResolved;
            _emuLibrary.Logger.Info($"[PSP] Scanning source \"{mapping.SourcePath}\" (destination \"{dstPath}\").");

            // Read each image's title id in parallel through the endpoint-aware scan concurrency governor (keyed
            // by the source dir). The read opens CHD/CSO/ISO containers — network round-trips that overlap across
            // images. The parse path is stateless across calls and IScanCache is internally locked, so the
            // workers are independent. GameMetadata construction runs single-threaded after the parallel phase.
            var imageFiles = EnumerateImageFiles(mapping.SourcePath).ToList();
            var infos = RunInspections(imageFiles, mapping.SourcePath,
                file => GetImageInfo(file), args.CancelToken);

            foreach (var info in infos)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;

                if (info == null)
                    continue;

                var title = BuildTitle(mapping, info, args.CancelToken);
                if (title == null)
                    continue;

                _emuLibrary.Logger.Debug($"[PSP] Title \"{title.Name}\" ({title.TitleId}) updates={title.Updates.Count} dlc={title.Dlcs.Count} from \"{info.Path}\".");

                var gameInfo = new PspGameInfo()
                {
                    MappingId = mapping.MappingId,
                    TitleId = title.TitleId,
                    SourceImagePath = RelativePath(title.SourceImagePath, mapping.SourcePath),
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

                var iconPath = ResolveIconPath(title);
                if (iconPath != null)
                    game.Icon = new MetadataFile(iconPath);

                yield return game;
            }
        }

        // Resolves the image's embedded ICON0.PNG into the shared per-title-id icon cache
        // (metadata/psp-icons/<titleId>.png), extracted once from the source image (re-read only on a cache
        // miss), fail-soft -> null. Read from the base image; updates/DLC payloads carry no game icon.
        private string ResolveIconPath(PspTitle title)
        {
            if (string.IsNullOrEmpty(title.TitleId) || string.IsNullOrEmpty(title.SourceImagePath))
                return null;

            return IconCache.EnsureIcon(
                _emuLibrary.GetPluginUserDataPath(), "psp-icons", title.TitleId, "png",
                () => PspImageReader.TryReadIcon(title.SourceImagePath, out var png) ? png : null,
                _emuLibrary.Logger);
        }

        private static IEnumerable<FileSystemInfoBase> EnumerateImageFiles(string sourcePath)
        {
            // Flat layout: images sit at the top level; the per-title-id folders beside them are payloads, not
            // images (and are never recursed into for image discovery).
            return new DirectoryInfo(sourcePath)
                .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
                .Where(f => ImageExtensions.Contains(f.Extension.TrimStart('.')))
                .Select(f => (FileSystemInfoBase)new FileInfoWrapper(f))
                .ToList();
        }

        // Re-derives one title's composite from the source (used by the scan and by the install/uninstall
        // controllers — the source may have changed since the scan). Reads the image title id (cached), then
        // pairs a sibling folder named for that title id as the update/DLC payload. Returns null if the image is
        // gone.
        internal PspTitle BuildTitle(EmulatorMapping mapping, string imageAbsPath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(imageAbsPath) || !File.Exists(imageAbsPath))
                return null;
            var info = GetImageInfo(new FileInfoWrapper(new FileInfo(imageAbsPath)));
            return info == null ? null : BuildTitle(mapping, info, ct);
        }

        private PspTitle BuildTitle(EmulatorMapping mapping, PspImageInfo info, CancellationToken ct)
        {
            if (info == null || string.IsNullOrEmpty(info.Path) || !File.Exists(info.Path))
                return null;

            var imageFile = new FileInfo(info.Path);
            var title = new PspTitle()
            {
                TitleId = NullIfEmpty(info.TitleId),
                SourceImagePath = info.Path,
                Version = NullIfEmpty(info.Version),
                Name = NullIfEmpty(info.Name)
                    ?? StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(imageFile.Name)),
                BaseRef = new PspContentRef()
                {
                    SourcePath = info.Path,
                    IsDirectory = false,
                    ContentSize = (ulong)imageFile.Length,
                },
            };

            ulong installSize = (ulong)imageFile.Length;

            // The update/DLC payload is a sibling folder named exactly for the title id — an O(1) existence check,
            // no enumeration. Everything observed routes to GAME; SAVEDATA routing is deferred/defensive.
            if (title.TitleId != null)
            {
                var payloadDir = Path.Combine(mapping.SourcePath, title.TitleId);
                if (Directory.Exists(payloadDir))
                {
                    var folderSize = GetDirectorySize(payloadDir, ct);
                    var item = new PspContentRef()
                    {
                        SourcePath = payloadDir,
                        IsDirectory = true,
                        Route = PspRoute.Game,
                        DestSubPath = title.TitleId,
                        Version = ReadPayloadVersion(payloadDir),
                        ContentSize = folderSize,
                    };
                    title.Updates.Add(item);
                    installSize += folderSize;

                    // Prefer the payload's version for display when the base image had none.
                    if (title.Version == null && item.Version > new Version(0, 0))
                        title.Version = item.Version.ToString();
                }
            }

            title.InstallSize = installSize;
            return title;
        }

        // Reads + caches an image's title-id/name/version (keyed by file stamp; nulls aren't cached). Never throws.
        private PspImageInfo GetImageInfo(FileSystemInfoBase file)
        {
            var stamp = FileStamp.FromFileSystemInfo(file);
            var cache = _emuLibrary.ScanCache;
            if (cache != null && cache.TryGet<PspImageInfo>(file.FullName, stamp, out var cached))
                return cached;

            PspImageInfo info = null;
            try
            {
                if (PspImageReader.TryReadParamSfo(file.FullName, out var sfo) && sfo != null)
                {
                    info = new PspImageInfo()
                    {
                        Path = file.FullName,
                        TitleId = NullIfEmpty(sfo.Get("DISC_ID")) ?? NullIfEmpty(sfo.TitleId),
                        Name = NullIfEmpty(sfo.Title),
                        Version = NullIfEmpty(sfo.Get("DISC_VERSION")) ?? NullIfEmpty(sfo.AppVer),
                    };
                }
                else
                {
                    // Not readable (unknown/damaged container) — still surface it as a base-only image so it can
                    // be copied and launched; the title id / payload just can't be resolved. Not cached (the
                    // read may succeed once the file settles).
                    info = new PspImageInfo() { Path = file.FullName };
                    return info;
                }
            }
            catch (Exception ex)
            {
                _emuLibrary.Logger.Warn(ex, $"[PSP] Failed to read image \"{file.FullName}\".");
                return new PspImageInfo() { Path = file.FullName };
            }

            if (info != null)
                cache?.Set(file.FullName, stamp, info);
            return info;
        }

        // A payload folder's version, read from its PBOOT.PBP PARAM.SFO (APP_VER) when present. This is the ONLY
        // thing that orders updates — never the folder/enumeration order. Version(0,0) when unreadable.
        private static Version ReadPayloadVersion(string payloadDir)
        {
            try
            {
                var pboot = Path.Combine(payloadDir, "PBOOT.PBP");
                if (File.Exists(pboot)
                    && PbpReader.TryReadParamSfoBytes(pboot, out var bytes)
                    && ParamSfo.TryParse(bytes, out var sfo)
                    && sfo != null)
                {
                    return Ps3FileInfo.ParseAppVer(sfo.AppVer ?? sfo.Version);
                }
            }
            catch
            {
                // fall through to the default
            }
            return new Version(0, 0);
        }

        // Installed iff the base image copy exists in DestinationPath AND each payload folder exists under the
        // resolved memory stick (Ps3/Xbox360 model — installed state derived from disk, never cached).
        private bool IsInstalled(EmulatorMapping mapping, PspTitle title, string dstPath, out string romPath, out string installDir)
        {
            romPath = null;
            installDir = null;

            if (string.IsNullOrEmpty(dstPath))
                return false;

            var installedImage = Path.Combine(dstPath, Path.GetFileName(title.SourceImagePath));
            installDir = dstPath;
            romPath = installedImage;
            if (!File.Exists(installedImage))
                return false;

            foreach (var item in title.Updates.Concat(title.Dlcs))
            {
                var dest = PpssppEmulator.GetContentDir(mapping, item);
                if (string.IsNullOrEmpty(dest) || !Directory.Exists(dest))
                    return false;
            }

            return true;
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

        // Runs the per-image parse over `items` through the endpoint-aware scan concurrency governor when one is
        // available, falling back to a sequential pass otherwise (mirrors Ps3Scanner/Xbox360Scanner).
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

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct) =>
            Enumerable.Empty<Game>();

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            // No legacy (pre-protobuf) PSP GameId format ever existed.
            gameInfo = null;
            return false;
        }
    }
}
