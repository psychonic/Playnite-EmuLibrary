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
