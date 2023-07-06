using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;

namespace EmuLibrary.RomTypes
{
    internal abstract class RomTypeScanner
    {
        public RomTypeScanner(IEmuLibrary emuLibrary) { }
        public abstract Guid LegacyPluginId { get; }

        public abstract RomType RomType { get; }

        public abstract bool TryGetGameInfoBaseFromLegacyGameId(Game game, out ELGameInfo gameInfo);
        public abstract IEnumerable<GameMetadata> GetGames(EmuLibrarySettings.ROMInstallerEmulatorMapping mapping, LibraryGetGamesArgs args);
        public abstract IEnumerable<Game> GetUninstalledGamesMissingSourceFiles();
    }
}
