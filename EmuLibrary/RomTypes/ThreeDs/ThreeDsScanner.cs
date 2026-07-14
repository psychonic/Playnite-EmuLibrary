using EmuLibrary.PlayniteCommon;
using EmuLibrary.Settings;
using EmuLibrary.Util;
using EmuLibrary.Util.Metadata;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmuLibrary.RomTypes.ThreeDs
{
    internal class ThreeDsScanner : RomTypeScanner
    {
        private readonly IEmuLibrary _emuLibrary;
        private readonly IPlayniteAPI _playniteAPI;

        public override RomType RomType => RomType.ThreeDs;

        // No legacy standalone extension to migrate from; a unique id is reserved here for future use.
        public override Guid LegacyPluginId => Guid.Parse("2018eb1e-64d9-4f54-ae9c-23906e1ebd33");

        // 3DS scans by its own format logic (loose extensions / CDN / CIA), not the emulator profile's image
        // extensions — Azahar's profile may declare none.
        public override bool RequiresProfileImageExtensions => false;

        // Loose files are copied to DestinationPath; content-store titles are decrypted into Azahar's SD store.
        // Either way a destination is configured.
        public override bool RequiresDestinationPath => true;

        public override IReadOnlyCollection<string> SupportedPlatformSpecIds => new[] { "nintendo_3ds" };

        // Loose bootable shapes Azahar reads directly — copied verbatim. The loose Z3DS variants are
        // also copied as-is (Azahar sniffs Z3DS). .cia/.zcia and CDN folders are content-store.
        private static readonly HashSet<string> s_looseExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".3ds", ".cci", ".cxi", ".3dsx", ".zcci", ".zcxi", ".z3dsx",
        };

        public ThreeDsScanner(IEmuLibrary emuLibrary) : base(emuLibrary)
        {
            _emuLibrary = emuLibrary;
            _playniteAPI = emuLibrary.Playnite;
        }

        public override IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args)
        {
            if (args.CancelToken.IsCancellationRequested)
                yield break;

            var srcPath = mapping.SourcePath;
            var dstPath = mapping.DestinationPathResolved;

            var installed = RunPhase(dstPath, () => GetLooseGames(mapping, dstPath, dstPath, installedPhase: true, args.CancelToken), args.CancelToken);
            foreach (var game in installed)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;
                yield return game;
            }

            var uninstalled = RunPhase(srcPath, () => GetLooseGames(mapping, srcPath, dstPath, installedPhase: false, args.CancelToken), args.CancelToken);
            foreach (var game in uninstalled)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;
                yield return game;
            }

            // Content-store titles (.cia/.zcia files and CDN folders) are enumerated from the source and their
            // installed-state is derived from Azahar's SD store.
            var contentStore = RunPhase(srcPath, () => GetContentStoreGames(mapping, args.CancelToken), args.CancelToken);
            foreach (var game in contentStore)
            {
                if (args.CancelToken.IsCancellationRequested)
                    yield break;
                yield return game;
            }
        }

        // Enumerates source .cia titles (grouped base + latest update + DLC) and marks each installed when its
        // base title is present in Azahar's SD store, resolving the launchable .app from the installed tmd.
        private List<GameMetadata> GetContentStoreGames(EmulatorMapping mapping, CancellationToken ct)
        {
            var games = new List<GameMetadata>();
            if (string.IsNullOrEmpty(mapping.SourcePath) || !Directory.Exists(mapping.SourcePath))
                return games;

            var azahar = new Azahar(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);
            var threeDsDb = new ThreeDsDb(_emuLibrary);

            foreach (var title in azahar.GetTitlesFromDir(mapping.SourcePath, ct))
            {
                if (ct.IsCancellationRequested)
                    return games;

                var gameInfo = new ThreeDsGameInfo()
                {
                    MappingId = mapping.MappingId,
                    TitleId = title.TitleId,
                    SourceName = Path.GetFileName(title.BaseRef.SourcePath),
                    BaseKind = ThreeDsBaseKind.ContentStore,
                };

                bool installed = azahar.IsTitleInstalled(title.TitleId);

                var game = new GameMetadata()
                {
                    Source = EmuLibrary.SourceName,
                    Name = title.Name,
                    IsInstalled = installed,
                    GameId = gameInfo.AsGameId(),
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    Regions = FileNameUtils.GuessRegionsFromRomName(title.Name).Select(r => new MetadataNameProperty(r)).ToHashSet<MetadataProperty>(),
                    InstallSize = title.InstallSize,
                    GameActions = new List<GameAction>() { new GameAction()
                    {
                        Name = $"Play in {mapping.Emulator.Name}",
                        Type = GameActionType.Emulator,
                        EmulatorId = mapping.EmulatorId,
                        EmulatorProfileId = mapping.EmulatorProfileId,
                        IsPlayAction = true,
                    } },
                };

                string installedBootApp = null;
                if (installed && azahar.TryGetInstalledRom(title.TitleId, out var romPath, out var size))
                {
                    game.InstallDirectory = MakePortable(azahar.Paths.TitleDir(title.TitleId));
                    game.Roms = new List<GameRom>() { new GameRom(title.Name, MakePortable(romPath)) };
                    game.InstallSize = size;
                    installedBootApp = romPath;
                }

                // The embedded SMDH gives the authoritative title + 48x48 icon. Installed titles read it from the
                // decrypted boot .app (keyless); uninstalled titles read it via a bounded decrypt of the source.
                var smdh = azahar.TryReadSmdh(title, mapping.SourcePath, installedBootApp, ct);

                // External enrichment, highest priority first. The SMDH title is only a name candidate when the
                // mapping opts in (it is menu-shortened by default); otherwise the 3dsdb name (title-id keyed)
                // shadows the filename, and the SMDH still provides the icon below regardless of this toggle.
                var smdhMeta = mapping.PreferInAppTitle && smdh?.Title != null ? new ExternalGameMetadata() { Name = smdh.Title } : null;
                threeDsDb.TryGet(title.TitleId, out var dbMeta);
                if (smdhMeta != null || dbMeta != null)
                    ExternalGameMetadata.Coalesce(smdhMeta, dbMeta).ApplyTo(game);

                // The SMDH 48x48 icon is the game's own icon — set it when we could read one.
                if (smdh?.IconBmp != null)
                    game.Icon = new Playnite.SDK.Models.MetadataFile("icon.bmp", smdh.IconBmp);

                games.Add(game);
            }

            return games;
        }

        private string MakePortable(string path)
        {
            if (string.IsNullOrEmpty(path) || !_playniteAPI.Paths.IsPortable)
                return path;
            return path.Replace(_playniteAPI.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
        }

        // Runs one enumeration phase through the endpoint-aware scan concurrency governor when available
        // (per-host + global permit), falling back to a direct call otherwise — same pattern as SingleFile.
        private List<GameMetadata> RunPhase(string endpointPath, Func<List<GameMetadata>> work, CancellationToken ct)
        {
            var governor = _emuLibrary.ScanConcurrency;
            if (governor != null)
                return governor.Run(endpointPath, work, ct);
            return work();
        }

        // Enumerates loose bootable files in one folder. For the installed phase, scanPath is the destination
        // and a match is reported installed; for the uninstalled phase, scanPath is the source and a match is
        // reported only when no copy already exists at the destination (installed-state derived from disk).
        private List<GameMetadata> GetLooseGames(EmulatorMapping mapping, string scanPath, string dstPath, bool installedPhase, CancellationToken ct)
        {
            var games = new List<GameMetadata>();
            if (string.IsNullOrEmpty(scanPath) || !Directory.Exists(scanPath))
                return games;

            // Loose files carry the same embedded SMDH (name + 48x48 icon) and title id as content-store titles,
            // so they get the same enrichment: the SMDH title/icon read straight from the NCCH (installed copies
            // are no_crypto; source dumps are decrypted with the user's keys), plus 3dsdb by title id.
            var azahar = new Azahar(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);
            var threeDsDb = new ThreeDsDb(_emuLibrary);

            foreach (var file in new SafeFileEnumerator(scanPath, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (ct.IsCancellationRequested)
                    return games;

                if (file.Attributes.HasFlag(FileAttributes.Directory) || !s_looseExtensions.Contains(Path.GetExtension(file.Name)))
                    continue;

                if (!installedPhase && File.Exists(Path.Combine(dstPath, file.Name)))
                    continue;

                var baseFileName = StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(file.Name));
                var gameName = StringExtensions.NormalizeGameName(baseFileName);

                var gameInfo = new ThreeDsGameInfo()
                {
                    MappingId = mapping.MappingId,
                    SourceName = file.Name,
                    BaseKind = ThreeDsBaseKind.LooseFile,
                };

                var game = new GameMetadata()
                {
                    Source = EmuLibrary.SourceName,
                    Name = gameName,
                    IsInstalled = installedPhase,
                    GameId = gameInfo.AsGameId(),
                    Platforms = new HashSet<MetadataProperty>() { new MetadataNameProperty(mapping.Platform.Name) },
                    Regions = FileNameUtils.GuessRegionsFromRomName(baseFileName).Select(r => new MetadataNameProperty(r)).ToHashSet<MetadataProperty>(),
                    InstallSize = (ulong)new FileInfo(file.FullName).Length,
                    GameActions = new List<GameAction>() { new GameAction()
                    {
                        Name = $"Play in {mapping.Emulator.Name}",
                        Type = GameActionType.Emulator,
                        EmulatorId = mapping.EmulatorId,
                        EmulatorProfileId = mapping.EmulatorProfileId,
                        IsPlayAction = true,
                    } },
                };

                if (installedPhase)
                {
                    var romPath = _playniteAPI.Paths.IsPortable
                        ? file.FullName.Replace(_playniteAPI.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory)
                        : file.FullName;
                    game.InstallDirectory = _playniteAPI.Paths.IsPortable
                        ? dstPath.Replace(_playniteAPI.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory)
                        : dstPath;
                    game.Roms = new List<GameRom>() { new GameRom(gameName, romPath) };
                }

                // Enrich from the embedded SMDH + 3dsdb. The parsed title id is used only for the db lookup — it
                // is deliberately not written into the GameId (loose ids stay TitleId=0 so already-imported loose
                // games keep their identity).
                var loose = azahar.TryReadLooseInfo(file.FullName, mapping.SourcePath, ct);
                if (loose != null)
                {
                    // SMDH title contributes to the name only when opted in (menu-shortened by default); the icon
                    // below is applied regardless of this toggle.
                    var smdhMeta = mapping.PreferInAppTitle && loose.Smdh?.Title != null ? new ExternalGameMetadata() { Name = loose.Smdh.Title } : null;
                    ExternalGameMetadata dbMeta = null;
                    if (loose.TitleId != 0)
                        threeDsDb.TryGet(loose.TitleId, out dbMeta);
                    if (smdhMeta != null || dbMeta != null)
                        ExternalGameMetadata.Coalesce(smdhMeta, dbMeta).ApplyTo(game);

                    if (loose.Smdh?.IconBmp != null)
                        game.Icon = new Playnite.SDK.Models.MetadataFile("icon.bmp", loose.Smdh.IconBmp);
                }

                games.Add(game);
            }

            return games;
        }

        // Re-derives a single title's composite from the source on demand (used by the install controller).
        internal ThreeDsTitle BuildTitle(EmulatorMapping mapping, ThreeDsGameInfo gameInfo, CancellationToken ct)
        {
            if (gameInfo.BaseKind == ThreeDsBaseKind.LooseFile)
                return BuildLooseTitle(mapping, gameInfo);

            // Content-store (.cia): re-derive the composite by base title id from the source.
            var azahar = new Azahar(mapping.EmulatorBasePathResolved, _emuLibrary.Logger, _emuLibrary.ScanCache);
            return azahar.BuildTitle(mapping.SourcePath, gameInfo.TitleId, ct);
        }

        private ThreeDsTitle BuildLooseTitle(EmulatorMapping mapping, ThreeDsGameInfo gameInfo)
        {
            if (string.IsNullOrEmpty(mapping.SourcePath) || string.IsNullOrEmpty(gameInfo.SourceName))
                return null;

            var fullPath = Path.Combine(mapping.SourcePath, gameInfo.SourceName);
            if (!File.Exists(fullPath))
                return null;

            var ext = Path.GetExtension(gameInfo.SourceName);
            var kind = ext.StartsWith(".z", StringComparison.OrdinalIgnoreCase) ? ThreeDsSourceKind.Z3dsLoose : ThreeDsSourceKind.LooseFile;
            var name = StringExtensions.NormalizeGameName(StringExtensions.GetPathWithoutAllExtensions(Path.GetFileName(gameInfo.SourceName)));

            return new ThreeDsTitle()
            {
                TitleId = gameInfo.TitleId,
                Name = name,
                BaseKind = ThreeDsBaseKind.LooseFile,
                InstallSize = (ulong)new FileInfo(fullPath).Length,
                BaseRef = new ThreeDsContentRef()
                {
                    SourcePath = fullPath,
                    SourceKind = kind,
                    TitleId = gameInfo.TitleId,
                    Version = 0,
                    ContentSize = (ulong)new FileInfo(fullPath).Length,
                },
            };
        }

        public override IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct) =>
            Enumerable.Empty<Game>();

        public override bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo)
        {
            gameInfo = null;
            return false;
        }
    }
}
