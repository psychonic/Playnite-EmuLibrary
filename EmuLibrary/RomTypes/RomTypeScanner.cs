using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes
{
    internal abstract class RomTypeScanner
    {
#pragma warning disable IDE0060 // Remove unused parameter
        public RomTypeScanner(IEmuLibrary emuLibrary) { }
#pragma warning restore IDE0060 // Remove unused parameter
        public abstract Guid LegacyPluginId { get; }

        public abstract RomType RomType { get; }

        public abstract bool TryGetGameInfoBaseFromLegacyGameId(Game game, EmuLibrarySettings.ROMInstallerEmulatorMapping mapping, out ELGameInfo gameInfo);
        public abstract IEnumerable<GameMetadata> GetGames(EmuLibrarySettings.ROMInstallerEmulatorMapping mapping, LibraryGetGamesArgs args);
        public abstract IEnumerable<Game> GetUninstalledGamesMissingSourceFiles();
    }
}
