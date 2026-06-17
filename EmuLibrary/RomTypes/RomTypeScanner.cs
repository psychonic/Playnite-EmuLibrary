using EmuLibrary.Settings;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;

namespace EmuLibrary.RomTypes
{
    internal abstract class RomTypeScanner
    {
#pragma warning disable IDE0060 // Remove unused parameter
        public RomTypeScanner(IEmuLibrary emuLibrary) { }
#pragma warning restore IDE0060 // Remove unused parameter
        public abstract Guid LegacyPluginId { get; }

        public abstract RomType RomType { get; }

        // Whether scanning a mapping of this RomType relies on the emulator profile's configured image
        // extensions. SingleFile/MultiFile enumerate the source by these extensions and so require at
        // least one. Types that scan by their own format logic (Yuzu, PS3) ignore them, and some
        // emulator profiles (e.g. RPCS3) declare none, so those override this to false.
        public virtual bool RequiresProfileImageExtensions => true;

        // Whether installing a mapping of this RomType uses the configured DestinationPath. SingleFile and
        // MultiFile copy the ROM there, and PS3 copies disc images there. Yuzu installs directly into the
        // emulator's NAND and ignores DestinationPath, so it overrides this to false — the settings UI then
        // disables the destination column for the mapping and the value isn't validated.
        public virtual bool RequiresDestinationPath => true;

        // Whether installing a mapping of this RomType can use Symlink/Hardlink instead of Copy (issue #2).
        // Only types that place the source verbatim at the destination qualify: SingleFile (one file) and
        // MultiFile (one folder). Types that transform the source on install - Yuzu (NAND import) and PS3
        // (package extraction / multi-content composite) - leave this false and always copy.
        public virtual bool SupportsInstallLinking => false;

        public abstract bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo);
        public virtual LegacySettingsMigrationResult MigrateLegacyPluginSettings(Plugin plugin, out EmulatorMapping mapping)
        {
            mapping = null;
            return LegacySettingsMigrationResult.Unnecessary;
        }
        public abstract IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args);
        public abstract IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct);
        
        protected static bool HasMatchingExtension(FileSystemInfoBase file, string extension)
        {
            return file.Extension.TrimStart('.').ToLower() == extension || (file.Extension == "" && extension == "<none>");
        }
    }
}
