using EmuLibrary.Settings;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
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

        public abstract bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmulatorMapping mapping, out ELGameInfo gameInfo);
        public virtual LegacySettingsMigrationResult MigrateLegacyPluginSettings(Plugin plugin, out EmulatorMapping mapping)
        {
            mapping = null;
            return LegacySettingsMigrationResult.Unnecessary;
        }
        public abstract IEnumerable<GameMetadata> GetGames(EmulatorMapping mapping, LibraryGetGamesArgs args);
        public abstract IEnumerable<Game> GetUninstalledGamesMissingSourceFiles(CancellationToken ct);
    }
}
