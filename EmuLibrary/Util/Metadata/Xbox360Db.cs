using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EmuLibrary.Util.Metadata
{
    // Xbox 360 metadata from the xenia-manager/x360db community archive (recreated Xbox Marketplace metadata).
    // The file is a single JSON array of game objects, each carrying the 8-hex title id in "id" and the game
    // name in "title" (plus a "boxart" cover URL we parse but, like the other sources, do not apply). It is
    // keyed by the parsed uint title id — the same value the scanner derives from a disc's XEX execution id or
    // an STFS header. Cached on disk, refreshed daily, fully fail-soft: any download/parse failure yields an
    // empty database, so the scanner keeps the names it derived from the files. Mirrors TitleDb / GameTdb.
    internal sealed class Xbox360Db
    {
        private const string Url = "https://raw.githubusercontent.com/xenia-manager/x360db/main/games.json";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

        private readonly IEmuLibrary _emuLibrary;
        private Dictionary<uint, ExternalGameMetadata> _byTitleId;

        public Xbox360Db(IEmuLibrary emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        public bool TryGet(uint titleId, out ExternalGameMetadata metadata)
        {
            EnsureLoaded();
            return _byTitleId.TryGetValue(titleId, out metadata);
        }

        // Lazy so an empty/cancelled scan never triggers a download.
        private void EnsureLoaded()
        {
            if (_byTitleId != null)
                return;
            _byTitleId = Load() ?? new Dictionary<uint, ExternalGameMetadata>();
        }

        private Dictionary<uint, ExternalGameMetadata> Load()
        {
            var logger = _emuLibrary.Logger;
            try
            {
                var localPath = Path.Combine(_emuLibrary.GetPluginUserDataPath(), "metadata", "x360db", "games.json");
                var path = CachedRemoteFile.Ensure(localPath, MaxAge, tmp => CachedRemoteFile.Download(Url, tmp, logger), logger);
                if (path == null)
                    return null;

                using (var fs = File.OpenRead(path))
                {
                    var dict = ParseStream(fs);
                    logger?.Info($"[Metadata] x360db loaded with {dict.Count} titles.");
                    return dict;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[Metadata] Failed to load x360db.");
                return null;
            }
        }

        // Streams the games.json array, one normalized record per object keyed by the parsed 8-hex "id". First
        // entry per title id wins. Exposed internally for unit testing.
        internal static Dictionary<uint, ExternalGameMetadata> ParseStream(Stream stream)
        {
            var result = new Dictionary<uint, ExternalGameMetadata>();
            var serializer = new JsonSerializer();

            using (var sr = new StreamReader(stream))
            using (var jr = new JsonTextReader(sr))
            {
                while (jr.Read())
                {
                    if (jr.TokenType != JsonToken.StartObject)
                        continue;

                    var entry = serializer.Deserialize<Xbox360DbEntry>(jr);
                    if (string.IsNullOrWhiteSpace(entry?.Id))
                        continue;
                    if (!uint.TryParse(entry.Id.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var titleId))
                        continue;
                    if (result.ContainsKey(titleId))
                        continue;

                    result[titleId] = entry.ToExternal();
                }
            }
            return result;
        }

        private sealed class Xbox360DbEntry
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("title")] public string Title { get; set; }
            [JsonProperty("boxart")] public string Boxart { get; set; }

            public ExternalGameMetadata ToExternal() => new ExternalGameMetadata
            {
                // x360db titles sometimes carry leading/trailing whitespace; trim so they don't surface as-is.
                Name = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
                CoverUrl = string.IsNullOrWhiteSpace(Boxart) ? null : Boxart.Trim(),
            };
        }
    }
}
