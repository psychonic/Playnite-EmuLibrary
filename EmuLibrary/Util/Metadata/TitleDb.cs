using Newtonsoft.Json;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EmuLibrary.Util.Metadata
{
    // Nintendo Switch metadata from blawar/titledb. The per-region JSON file is one big object keyed by nsuId;
    // each entry carries the 16-hex application title id in its "id" field, which is what we index by (the same
    // value Yuzu persists as YuzuGameInfo.TitleId). The file is cached on disk and refreshed daily, and the
    // whole thing is fail-soft: any download/parse failure yields an empty database, so the scanner simply
    // keeps the names it derived from the game files.
    //
    // (GameTDB also lists Switch games, but keys them by a cart serial that loose NSP/XCI dumps don't carry, so
    // it can't be joined to our title ids -- titledb is the only usable Switch source. See GameTdb.)
    internal sealed class TitleDb
    {
        private const string UrlFormat = "https://raw.githubusercontent.com/blawar/titledb/master/{0}.json";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

        private readonly IEmuLibrary _emuLibrary;
        private Dictionary<ulong, ExternalGameMetadata> _byTitleId;

        public TitleDb(IEmuLibrary emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        public bool TryGet(ulong titleId, out ExternalGameMetadata metadata)
        {
            EnsureLoaded();
            return _byTitleId.TryGetValue(titleId, out metadata);
        }

        // Loads the language-appropriate file once, falling back to English if that file is unavailable, then
        // to an empty database. Lazy so an empty/cancelled scan never triggers a download.
        private void EnsureLoaded()
        {
            if (_byTitleId != null)
                return;

            var lang = _emuLibrary.Playnite?.ApplicationSettings?.Language;
            var file = MetadataLanguage.TitleDbFile(lang);

            _byTitleId = Load(file)
                ?? (file != MetadataLanguage.DefaultTitleDbFile ? Load(MetadataLanguage.DefaultTitleDbFile) : null)
                ?? new Dictionary<ulong, ExternalGameMetadata>();
        }

        // Returns null when the file couldn't be made available or parsed (so the caller can try English).
        private Dictionary<ulong, ExternalGameMetadata> Load(string fileBaseName)
        {
            var logger = _emuLibrary.Logger;
            try
            {
                var localPath = Path.Combine(_emuLibrary.GetPluginUserDataPath(), "metadata", "titledb", fileBaseName + ".json");
                var url = string.Format(UrlFormat, fileBaseName);
                var path = CachedRemoteFile.Ensure(localPath, MaxAge, tmp => CachedRemoteFile.Download(url, tmp, logger), logger);
                if (path == null)
                    return null;

                using (var fs = File.OpenRead(path))
                {
                    var dict = ParseStream(fs);
                    logger?.Info($"[Metadata] titledb \"{fileBaseName}\" loaded with {dict.Count} titles.");
                    return dict;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[Metadata] Failed to load titledb \"{fileBaseName}\".");
                return null;
            }
        }

        // Streams the (large) titledb region file, extracting one normalized record per entry keyed by the
        // parsed 16-hex "id". First entry per title id wins. Exposed internally for unit testing.
        internal static Dictionary<ulong, ExternalGameMetadata> ParseStream(Stream stream)
        {
            var result = new Dictionary<ulong, ExternalGameMetadata>();
            var serializer = new JsonSerializer();

            using (var sr = new StreamReader(stream))
            using (var jr = new JsonTextReader(sr))
            {
                while (jr.Read())
                {
                    if (jr.TokenType != JsonToken.PropertyName)
                        continue;
                    if (!jr.Read())
                        break;
                    if (jr.TokenType != JsonToken.StartObject)
                    {
                        jr.Skip();
                        continue;
                    }

                    var entry = serializer.Deserialize<TitleDbEntry>(jr);
                    if (string.IsNullOrWhiteSpace(entry?.Id))
                        continue;
                    if (!ulong.TryParse(entry.Id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var titleId))
                        continue;
                    if (result.ContainsKey(titleId))
                        continue;

                    result[titleId] = entry.ToExternal();
                }
            }
            return result;
        }

        // titledb release dates are integers like 20171027 (yyyymmdd); 0/null/garbage => no date. Exposed for
        // unit testing.
        internal static ReleaseDate? ParseReleaseDate(long? yyyymmdd)
        {
            if (yyyymmdd == null || yyyymmdd <= 0)
                return null;

            var v = yyyymmdd.Value;
            int year = (int)(v / 10000);
            int month = (int)(v / 100 % 100);
            int day = (int)(v % 100);

            if (year < 1970 || year > 3000)
                return null;

            bool hasMonth = month >= 1 && month <= 12;
            bool hasDay = day >= 1 && day <= 31;

            if (hasMonth && hasDay)
                return new ReleaseDate(year, month, day);
            if (hasMonth)
                return new ReleaseDate(year, month);
            return new ReleaseDate(year);
        }

        private sealed class TitleDbEntry
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("publisher")] public string Publisher { get; set; }
            [JsonProperty("developer")] public string Developer { get; set; }
            [JsonProperty("description")] public string Description { get; set; }
            [JsonProperty("category")] public List<string> Category { get; set; }
            [JsonProperty("releaseDate")] public long? ReleaseDate { get; set; }
            [JsonProperty("iconUrl")] public string IconUrl { get; set; }
            [JsonProperty("bannerUrl")] public string BannerUrl { get; set; }

            public ExternalGameMetadata ToExternal() => new ExternalGameMetadata
            {
                Name = Name,
                Description = Description,
                Developers = string.IsNullOrWhiteSpace(Developer) ? null : new[] { Developer },
                Publishers = string.IsNullOrWhiteSpace(Publisher) ? null : new[] { Publisher },
                Genres = Category != null && Category.Count > 0 ? Category : null,
                ReleaseDate = ParseReleaseDate(ReleaseDate),
                IconUrl = IconUrl,
                BackgroundUrl = BannerUrl,
            };
        }
    }
}
