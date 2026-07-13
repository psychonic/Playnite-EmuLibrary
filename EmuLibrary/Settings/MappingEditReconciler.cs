using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmuLibrary.Settings
{
    // Helpers for reconciling already-imported games when the user edits a mapping. A game carries its
    // owning mapping's Id inside its serialized GameId (ELGameInfo.MappingId), and the mapping is resolved
    // live by that Id - so most per-game state (emulator/profile/platform, name) is baked into the Playnite
    // database at first import and is NOT refreshed on a rescan. These helpers let the settings UI warn
    // about identity-breaking edits and push emulator/platform changes onto the existing games.
    internal static class MappingEditReconciler
    {
        // The games in the Playnite database that belong to a given mapping. Filters to EmuLibrary games in
        // the current ("!"-prefixed) GameId format and decodes each one's MappingId, tolerating any game whose
        // GameId can't be decoded (legacy/foreign entries) by skipping it.
        internal static IEnumerable<Game> GamesForMapping(IPlayniteAPI api, Guid mappingId)
        {
            foreach (var game in api.Database.Games)
            {
                if (game.PluginId != EmuLibrary.PluginId)
                    continue;
                if (string.IsNullOrEmpty(game.GameId) || !game.GameId.StartsWith("!"))
                    continue;

                Guid gameMappingId;
                try
                {
                    gameMappingId = game.GetELGameInfo().MappingId;
                }
                catch
                {
                    continue;
                }

                if (gameMappingId == mappingId)
                    yield return game;
            }
        }

        // Re-points the mapping's already-imported games at its current emulator/profile/platform, matching
        // what a fresh scan would have written. Resolves the mapping's platform to a Playnite platform id
        // (find-or-created by name, mirroring the scanner's MetadataNameProperty) and delegates the rewrite
        // to RepointGames. Returns the number of games updated.
        internal static int ApplyEmulatorPlatformMigration(IPlayniteAPI api, EmulatorMapping mapping, ILogger logger)
        {
            Guid? platformId = null;
            var platformName = mapping.Platform?.Name;
            if (!string.IsNullOrEmpty(platformName))
            {
                var platform = api.Database.Platforms.FirstOrDefault(p => string.Equals(p.Name, platformName, StringComparison.OrdinalIgnoreCase))
                    ?? api.Database.Platforms.Add(platformName);
                platformId = platform.Id;
            }

            return RepointGames(api, mapping.MappingId, mapping.EmulatorId, mapping.EmulatorProfileId, platformId, logger);
        }

        // Rewrites every emulator play action's EmulatorId/EmulatorProfileId and (when platformId is given)
        // replaces the platform on each of the mapping's games, inside a buffered update. Takes the already
        // resolved target values so it has no dependency on live settings/emulation resolution - which is
        // also what makes it unit-testable against a fake database. Returns the number of games updated.
        internal static int RepointGames(IPlayniteAPI api, Guid mappingId, Guid emulatorId, string emulatorProfileId, Guid? platformId, ILogger logger)
        {
            var games = GamesForMapping(api, mappingId).ToList();
            if (games.Count == 0)
                return 0;

            using (api.Database.BufferedUpdate())
            {
                foreach (var game in games)
                {
                    if (game.GameActions != null)
                    {
                        foreach (var action in game.GameActions.Where(a => a.Type == GameActionType.Emulator))
                        {
                            action.EmulatorId = emulatorId;
                            action.EmulatorProfileId = emulatorProfileId;
                        }
                    }

                    if (platformId.HasValue)
                        game.PlatformIds = new List<Guid> { platformId.Value };

                    api.Database.Games.Update(game);
                }
            }

            logger?.Info($"Re-pointed emulator/platform for {games.Count} game(s) of mapping {mappingId}.");
            return games.Count;
        }
    }
}
