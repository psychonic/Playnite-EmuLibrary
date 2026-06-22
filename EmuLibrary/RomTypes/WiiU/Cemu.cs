using EmuLibrary.PlayniteCommon;
using EmuLibrary.RomTypes.WiiU.Crypto;
using EmuLibrary.Util.ScanCache;
using EmuLibrary.Util.ZArchive;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace EmuLibrary.RomTypes.WiiU
{
    // Emulator-facing helper for Cemu/Wii U. Unlike the old CemuLibrary this never touches Cemu's NAND: it
    // enumerates source titles, resolves their composite (base + latest update + DLC) from the supported
    // source formats, converts to .wua when needed, and derives installed state from the destination folder.
    //
    // Supports every Wii U source format: NUS/WUP dumps, decrypted loadiine folders, encrypted disc images
    // (.wux/.wud + sibling .key), and pre-made .wua archives (read via the ZArchive reader). A .wua is already
    // a self-contained, decrypted bundle, so it surfaces as one Game unit and installs by a plain copy.
    internal sealed class Cemu
    {
        // Per-content-unit locator info, cached (JSON) keyed by the unit's identifying file stamp.
        public sealed class UnitInfo
        {
            public string Path { get; set; }
            public WiiUSourceFormat Format { get; set; }
            public WiiUContentKind Kind { get; set; }
            public ulong BaseTitleId { get; set; }
            public ulong TitleId { get; set; }
            public uint Version { get; set; }
            public ulong ContentSize { get; set; }
        }

        private readonly string _basePath;
        private readonly ILogger _logger;
        private readonly IScanCache _scanCache;
        private byte[] _commonKey;

        public Cemu(string basePath, ILogger logger, IScanCache scanCache = null)
        {
            _basePath = basePath;
            _logger = logger;
            _scanCache = scanCache;
        }

        public string KeysPath => Path.Combine(_basePath, "keys.txt");

        public byte[] CommonKey => _commonKey ?? (_commonKey = WiiUKeys.LoadCommonKey(KeysPath));

        #region Source enumeration

        // Resolves every Wii U title found under `sourcePath` into a composite (base + latest update + DLC).
        public IEnumerable<WiiUTitle> GetTitlesFromDir(string sourcePath, CancellationToken ct)
        {
            var units = EnumerateUnits(sourcePath, ct).ToList();

            var byBase = new Dictionary<ulong, List<UnitInfo>>();
            foreach (var u in units)
            {
                if (!IsContentTitleId(u.TitleId))
                    continue; // skip system titles (e.g. the on-disc system-update partition newer discs carry)

                if (!byBase.TryGetValue(u.BaseTitleId, out var list))
                    byBase[u.BaseTitleId] = list = new List<UnitInfo>();
                list.Add(u);
            }

            foreach (var kv in byBase)
            {
                ct.ThrowIfCancellationRequested();

                var group = kv.Value;
                var baseUnit = group.FirstOrDefault(x => x.Kind == WiiUContentKind.Game);
                if (baseUnit == null)
                    continue; // updates/DLC with no base in the source — nothing to install

                var update = CompositeContent.SelectUpdatesToInstall(
                    group.Where(x => x.Kind == WiiUContentKind.Update),
                    UpdateInstallStrategy.InstallLatestUpdateOnly,
                    x => x.Version).FirstOrDefault();

                var dlc = group.Where(x => x.Kind == WiiUContentKind.Dlc)
                    .GroupBy(x => x.TitleId).Select(g => g.First()).ToList();

                var meta = TryGetMeta(baseUnit);

                var title = new WiiUTitle
                {
                    TitleId = kv.Key,
                    Name = meta?.Name ?? $"{kv.Key:x16}",
                    ProductCode = meta?.ProductCode,
                    GameTdbId = BuildGameTdbId(meta?.ProductCode, meta?.CompanyCode),
                    Version = (update?.Version ?? baseUnit.Version).ToString(),
                    InstallSize = baseUnit.ContentSize + (update?.ContentSize ?? 0) + (ulong)dlc.Sum(d => (decimal)d.ContentSize),
                    BaseRef = ToRef(baseUnit),
                    UpdateRef = update != null ? ToRef(update) : null,
                    DlcRefs = dlc.Select(ToRef).ToList(),
                };

                yield return title;
            }
        }

        // Re-derives a single title by base id from the source (used by the install controller at install time,
        // exactly like Ps3Scanner/YuzuScanner re-scan). Returns null if not found.
        public WiiUTitle BuildTitle(string sourcePath, ulong baseTitleId, CancellationToken ct) =>
            GetTitlesFromDir(sourcePath, ct).FirstOrDefault(t => t.TitleId == baseTitleId);

        private IEnumerable<UnitInfo> EnumerateUnits(string sourcePath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
                yield break;

            foreach (var entry in new SafeFileEnumerator(sourcePath, "*", SearchOption.TopDirectoryOnly))
            {
                if (ct.IsCancellationRequested)
                    yield break;

                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    var dir = entry.FullName;
                    if (File.Exists(Path.Combine(dir, "title.tmd")))
                    {
                        var info = GetNusUnit(dir);
                        if (info != null)
                            yield return info;
                    }
                    else if (IsLoadiine(dir))
                    {
                        var info = GetLoadiineUnit(dir);
                        if (info != null)
                            yield return info;
                    }
                }
                else
                {
                    var ext = entry.Extension.ToLowerInvariant();
                    if (ext == ".wux" || ext == ".wud")
                    {
                        foreach (var u in GetDiscUnits(entry.FullName, ext == ".wux" ? WiiUSourceFormat.Wux : WiiUSourceFormat.Wud))
                            yield return u;
                    }
                    else if (ext == ".wua")
                    {
                        foreach (var u in GetWuaUnits(entry.FullName))
                            yield return u;
                    }
                }
            }
        }

        private UnitInfo GetNusUnit(string dir)
        {
            var tmdPath = Path.Combine(dir, "title.tmd");
            FileStamp stamp = default;
            bool haveStamp = FileStamp.TryCapture(tmdPath, out stamp);
            if (haveStamp && _scanCache != null && _scanCache.TryGet<UnitInfo>(dir, stamp, out var cached))
                return cached;

            try
            {
                var tmd = TitleMetadata.Parse(File.ReadAllBytes(tmdPath));
                var info = new UnitInfo
                {
                    Path = dir,
                    Format = WiiUSourceFormat.Nus,
                    Kind = tmd.Kind,
                    BaseTitleId = tmd.BaseTitleId,
                    TitleId = tmd.TitleId,
                    Version = tmd.TitleVersion,
                    ContentSize = (ulong)tmd.Contents.Sum(c => (decimal)c.Size),
                };
                if (haveStamp)
                    _scanCache?.Set(dir, stamp, info);
                return info;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[WiiU] Failed to read NUS title at \"{dir}\".");
                return null;
            }
        }

        // Cached list of game-partition units for one disc image (a disc can hold more than one game
        // partition, though typically just one).
        public sealed class DiscUnits
        {
            public List<UnitInfo> Units { get; set; }
        }

        private List<UnitInfo> GetDiscUnits(string discPath, WiiUSourceFormat format)
        {
            var result = new List<UnitInfo>();

            var keyPath = ResolveDiscKeyPath(discPath);
            if (keyPath == null)
            {
                _logger?.Warn($"[WiiU] Skipping disc \"{discPath}\": no matching .key file found.");
                return result;
            }

            bool haveStamp = FileStamp.TryCapture(discPath, out var stamp);
            if (haveStamp && _scanCache != null && _scanCache.TryGet<DiscUnits>(discPath, stamp, out var cached))
                return cached.Units;

            try
            {
                using (var disc = WiiUDisc.Open(discPath, keyPath))
                {
                    foreach (var p in disc.GamePartitions)
                    {
                        ulong contentSize = 0;
                        try { contentSize = (ulong)TitleMetadata.Parse(p.RawTmd).Contents.Sum(c => (decimal)c.Size); }
                        catch { /* size is informational */ }

                        result.Add(new UnitInfo
                        {
                            Path = discPath,
                            Format = format,
                            Kind = p.Kind,
                            BaseTitleId = p.TitleId & 0xFFFFFF00FFFFFFFFUL,
                            TitleId = p.TitleId,
                            Version = p.TitleVersion,
                            ContentSize = contentSize,
                        });
                    }
                }
                if (haveStamp)
                    _scanCache?.Set(discPath, stamp, new DiscUnits { Units = result });
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[WiiU] Failed to read disc \"{discPath}\".");
            }

            return result;
        }

        // The Wii U disc key sits beside the image as "<image>.key" (or "game.key"), matching the old
        // CemuLibrary convention.
        public static string ResolveDiscKeyPath(string discPath)
        {
            var dir = Path.GetDirectoryName(discPath);
            var baseName = Path.GetFileNameWithoutExtension(discPath);
            foreach (var cand in new[] { Path.Combine(dir, baseName + ".key"), Path.Combine(dir, "game.key") })
                if (File.Exists(cand))
                    return cand;
            return null;
        }

        // Cached list of game units inside one pre-made .wua. A .wua already bundles base + update + DLC in
        // "<titleId>_v<version>" folders, so we only surface the Game folder(s); the merged update/DLC stay
        // inside the file (it installs by a plain copy, no re-merge needed).
        public sealed class WuaUnits
        {
            public List<UnitInfo> Units { get; set; }
        }

        private List<UnitInfo> GetWuaUnits(string wuaPath)
        {
            bool haveStamp = FileStamp.TryCapture(wuaPath, out var stamp);
            if (haveStamp && _scanCache != null && _scanCache.TryGet<WuaUnits>(wuaPath, stamp, out var cached))
                return cached.Units;

            var result = new List<UnitInfo>();
            try
            {
                ulong fileSize = (ulong)new FileInfo(wuaPath).Length;
                using (var reader = ZArchiveReader.Open(wuaPath))
                {
                    foreach (var folder in reader.ListSubdirectories(""))
                    {
                        if (!TryParseWuaFolder(folder, out var titleId, out var version))
                            continue;
                        if (KindFromTitleId(titleId) != WiiUContentKind.Game)
                            continue; // updates/DLC are merged inside; the Game folder represents the whole title

                        result.Add(new UnitInfo
                        {
                            Path = wuaPath,
                            Format = WiiUSourceFormat.Wua,
                            Kind = WiiUContentKind.Game,
                            BaseTitleId = titleId & 0xFFFFFF00FFFFFFFFUL,
                            TitleId = titleId,
                            Version = version,
                            ContentSize = fileSize,
                        });
                    }
                }
                if (haveStamp)
                    _scanCache?.Set(wuaPath, stamp, new WuaUnits { Units = result });
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[WiiU] Failed to read .wua \"{wuaPath}\".");
            }
            return result;
        }

        // Parse a .wua top-level folder name "<titleId16hex>_v<version>" (e.g. "0005000010102000_v16").
        private static bool TryParseWuaFolder(string folder, out ulong titleId, out uint version)
        {
            titleId = 0;
            version = 0;
            int sep = folder.IndexOf("_v", StringComparison.OrdinalIgnoreCase);
            if (sep <= 0)
                return false;
            var idPart = folder.Substring(0, sep);
            var verPart = folder.Substring(sep + 2);
            return ulong.TryParse(idPart, System.Globalization.NumberStyles.HexNumber, null, out titleId)
                && uint.TryParse(verPart, out version);
        }

        private UnitInfo GetLoadiineUnit(string dir)
        {
            var metaPath = Path.Combine(dir, "meta", "meta.xml");
            FileStamp stamp = default;
            bool haveStamp = FileStamp.TryCapture(metaPath, out stamp);
            if (haveStamp && _scanCache != null && _scanCache.TryGet<UnitInfo>(dir, stamp, out var cached))
                return cached;

            try
            {
                var meta = ParseMeta(File.ReadAllText(metaPath));
                if (meta == null || meta.TitleId == 0)
                    return null;

                var info = new UnitInfo
                {
                    Path = dir,
                    Format = WiiUSourceFormat.Loadiine,
                    Kind = KindFromTitleId(meta.TitleId),
                    BaseTitleId = meta.TitleId & 0xFFFFFF00FFFFFFFFUL,
                    TitleId = meta.TitleId,
                    Version = meta.Version,
                    ContentSize = 0,
                };
                if (haveStamp)
                    _scanCache?.Set(dir, stamp, info);
                return info;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[WiiU] Failed to read loadiine title at \"{dir}\".");
                return null;
            }
        }

        private static bool IsLoadiine(string dir) =>
            Directory.Exists(Path.Combine(dir, "code")) &&
            File.Exists(Path.Combine(dir, "meta", "meta.xml"));

        // A Wii U title id's high 32 bits are its type. Only application content belongs in a library:
        // application (0x00050000), DLC (0x0005000C) and update/patch (0x0005000E). Everything else is a
        // system title — most notably the system-update partition (id 0x0005001010060000) that many later
        // retail discs ship alongside the game. Those must be skipped: their base id masks to a game-type id
        // (0x0005000010060000) with no meta.xml longname, so they otherwise collapse into one phantom "game"
        // shown as its title id.
        internal static bool IsContentTitleId(ulong titleId)
        {
            switch ((uint)(titleId >> 32))
            {
                case 0x00050000: // application (game)
                case 0x0005000C: // DLC
                case 0x0005000E: // update / patch
                    return true;
                default:
                    return false;
            }
        }

        private static WiiUContentKind KindFromTitleId(ulong titleId)
        {
            switch ((byte)((titleId >> 32) & 0xFF))
            {
                case 0x0E: return WiiUContentKind.Update;
                case 0x0C: return WiiUContentKind.Dlc;
                default: return WiiUContentKind.Game;
            }
        }

        private static WiiUContentRef ToRef(UnitInfo u) => new WiiUContentRef
        {
            SourcePath = u.Path,
            Format = u.Format,
            Kind = u.Kind,
            TitleId = u.TitleId,
            Version = u.Version,
        };

        #endregion

        #region meta.xml

        public sealed class MetaInfo
        {
            public string Name;
            public string ProductCode;   // e.g. "WUP-P-AVEE"
            public string CompanyCode;   // e.g. "020W"
            public ulong TitleId;
            public uint Version;
        }

        // GameTDB's Wii U database is keyed by a 6-character id = 4-char game code + 2-char maker code (the same
        // scheme as Wii/GameCube), e.g. "AVEE0W". meta.xml carries both pieces: product_code ("WUP-P-AVEE" ->
        // game code "AVEE", the part after the last '-') and company_code ("020W" -> maker "0W", the last two
        // chars). Returns null when either piece is missing so the caller simply skips GameTDB enrichment.
        // (This is why Wii U CAN use GameTDB while Switch can't: the join key is embedded in the file.)
        public static string BuildGameTdbId(string productCode, string companyCode)
        {
            if (string.IsNullOrWhiteSpace(productCode) || string.IsNullOrWhiteSpace(companyCode))
                return null;

            var pc = productCode.Trim();
            int dash = pc.LastIndexOf('-');
            var gameCode = (dash >= 0 ? pc.Substring(dash + 1) : pc).Trim();

            var cc = companyCode.Trim();
            if (gameCode.Length < 4 || cc.Length < 2)
                return null;

            var maker = cc.Substring(cc.Length - 2);
            return (gameCode + maker).ToUpperInvariant();
        }

        private MetaInfo TryGetMeta(UnitInfo baseUnit)
        {
            try
            {
                if (baseUnit.Format == WiiUSourceFormat.Loadiine)
                    return ParseMeta(File.ReadAllText(Path.Combine(baseUnit.Path, "meta", "meta.xml")));

                if (baseUnit.Format == WiiUSourceFormat.Nus)
                {
                    using (var reader = new NusReader(baseUnit.Path, CommonKey))
                    {
                        var xml = reader.ReadMetaXml();
                        return xml != null ? ParseMeta(xml) : null;
                    }
                }

                if (baseUnit.Format == WiiUSourceFormat.Wua)
                {
                    using (var reader = ZArchiveReader.Open(baseUnit.Path))
                    {
                        var metaPath = $"{baseUnit.TitleId:x16}_v{baseUnit.Version}/meta/meta.xml";
                        if (reader.TryReadFile(metaPath, out var bytes))
                            return ParseMeta(Encoding.UTF8.GetString(bytes));
                        return null;
                    }
                }

                if (baseUnit.Format == WiiUSourceFormat.Wux || baseUnit.Format == WiiUSourceFormat.Wud)
                {
                    var keyPath = ResolveDiscKeyPath(baseUnit.Path);
                    if (keyPath == null)
                        return null;
                    using (var disc = WiiUDisc.Open(baseUnit.Path, keyPath))
                    {
                        var part = disc.GamePartitions.FirstOrDefault(p => p.TitleId == baseUnit.TitleId)
                            ?? disc.GamePartitions.FirstOrDefault();
                        if (part == null)
                            return null;
                        using (var reader = new NusReader(new WudContentSource(disc.Reader, part), CommonKey))
                        {
                            var xml = reader.ReadMetaXml();
                            return xml != null ? ParseMeta(xml) : null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[WiiU] Failed to read meta.xml for \"{baseUnit.Path}\".");
            }
            return null;
        }

        // Wii U meta.xml: <menu><longname_en>..</longname_en><product_code>WUP-N-XXXX</product_code>
        //                 <title_id>0005..</title_id><title_version>..</title_version></menu>
        internal static MetaInfo ParseMeta(string xml)
        {
            if (string.IsNullOrEmpty(xml))
                return null;

            // Wii U meta.xml is UTF-8 WITH a byte-order mark. The decrypted NUS/.wua bytes are decoded via
            // Encoding.UTF8.GetString, which keeps the BOM as a leading U+FEFF that XmlDocument.LoadXml rejects
            // ("Data at the root level is invalid. Line 1, position 1."). Strip a leading BOM (and any stray
            // whitespace) before parsing. (The loadiine path uses File.ReadAllText, which strips the BOM itself.)
            xml = xml.TrimStart('\uFEFF').TrimStart();

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var menu = doc["menu"];
            if (menu == null)
                return null;

            string Text(string tag) => menu[tag]?.InnerText?.Trim();

            var name = Text("longname_en");
            if (!string.IsNullOrWhiteSpace(name))
                name = Regex.Replace(name, @"\s+", " ");

            ulong titleId = 0;
            var tidText = Text("title_id");
            if (!string.IsNullOrWhiteSpace(tidText))
                ulong.TryParse(tidText, System.Globalization.NumberStyles.HexNumber, null, out titleId);

            uint version = 0;
            uint.TryParse(Text("title_version"), out version);

            return new MetaInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                ProductCode = Text("product_code"),
                CompanyCode = Text("company_code"),
                TitleId = titleId,
                Version = version,
            };
        }

        #endregion

        #region Installed-state derivation (from destination)

        private static readonly Regex InstalledFolderRegex =
            new Regex(@"^(?<name>.+) \[(?<tid>[0-9a-fA-F]{16})\]$", RegexOptions.Compiled);

        // The per-title destination folder name we install into, e.g. "Super Mario [0005000010102000]". The
        // base title id is embedded so installed state (and the GameId) can be recovered from disk.
        public static string DestinationFolderName(string name, ulong baseTitleId) =>
            $"{SanitizeFileName(name)} [{baseTitleId:x16}]";

        public IEnumerable<WiiUInstalledTitle> GetInstalledTitles(string destPath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(destPath) || !Directory.Exists(destPath))
                yield break;

            foreach (var entry in new SafeFileEnumerator(destPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (ct.IsCancellationRequested)
                    yield break;
                if (!entry.Attributes.HasFlag(FileAttributes.Directory))
                    continue;

                var m = InstalledFolderRegex.Match(entry.Name);
                if (!m.Success)
                    continue;

                ulong titleId = ulong.Parse(m.Groups["tid"].Value, System.Globalization.NumberStyles.HexNumber);
                var (launch, format) = FindLaunchable(entry.FullName);
                if (launch == null)
                    continue; // incomplete install

                yield return new WiiUInstalledTitle
                {
                    TitleId = titleId,
                    Name = m.Groups["name"].Value,
                    Version = null,
                    InstallSize = DirectorySize(entry.FullName),
                    LaunchPath = launch,
                    Format = format,
                    InstalledPath = entry.FullName,
                };
            }
        }

        // Cemu launches a .wua / .wux directly, or a loadiine code/*.rpx.
        private static (string launch, WiiUSourceFormat format) FindLaunchable(string dir)
        {
            var wua = Directory.GetFiles(dir, "*.wua", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (wua != null)
                return (wua, WiiUSourceFormat.Wua);

            var wux = Directory.GetFiles(dir, "*.wux", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (wux != null)
                return (wux, WiiUSourceFormat.Wux);

            var wud = Directory.GetFiles(dir, "*.wud", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (wud != null)
                return (wud, WiiUSourceFormat.Wud);

            var codeDir = Path.Combine(dir, "code");
            if (Directory.Exists(codeDir))
            {
                var rpx = Directory.GetFiles(codeDir, "*.rpx", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (rpx != null)
                    return (rpx, WiiUSourceFormat.Loadiine);
            }
            return (null, default);
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

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        #endregion
    }
}
