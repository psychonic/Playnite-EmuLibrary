using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EmuLibrary.Util.Metadata
{
    // Nintendo 3DS metadata from hax0kartik/3dsdb. The database is published as one JSON array per region
    // (jsons/list_US.json, list_GB.json, list_JP.json, list_KR.json, list_TW.json); each entry carries the
    // 16-hex application title id in its "TitleID" field, which is what we index by (the same value the 3DS
    // scanner derives from a title's tmd). 3DS has no clean GameTDB/titledb join (those key by cart serial /
    // nsuId, which loose CIA/CDN dumps don't carry), so this title-id-keyed list is the usable source.
    //
    // Region files are loaded in language-preference order and merged into one map; the first region to list a
    // title wins, and that region's label is attached. Each file is cached on disk, refreshed daily, and the
    // whole thing is fail-soft: any download/parse failure yields an empty database, so the scanner simply keeps
    // the names it derived from the game files (or the SMDH).
    internal sealed class ThreeDsDb
    {
        private const string UrlFormat = "https://raw.githubusercontent.com/hax0kartik/3dsdb/master/jsons/list_{0}.json";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

        // 3dsdb region code -> the region label used elsewhere (matches FileNameUtils' guessed regions).
        private static readonly Dictionary<string, string> RegionLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["US"] = "USA",
                ["GB"] = "Europe",
                ["JP"] = "Japan",
                ["KR"] = "Korea",
                ["TW"] = "Taiwan",
            };

        private readonly IEmuLibrary _emuLibrary;
        private Dictionary<ulong, ExternalGameMetadata> _byTitleId;

        public ThreeDsDb(IEmuLibrary emuLibrary)
        {
            _emuLibrary = emuLibrary;
        }

        public bool TryGet(ulong titleId, out ExternalGameMetadata metadata)
        {
            EnsureLoaded();
            return _byTitleId.TryGetValue(titleId, out metadata);
        }

        // Loads each region file in preference order once and merges them (first region to list a title wins).
        // Lazy so an empty/cancelled scan never triggers a download.
        private void EnsureLoaded()
        {
            if (_byTitleId != null)
                return;

            var merged = new Dictionary<ulong, ExternalGameMetadata>();
            foreach (var region in RegionOrder(_emuLibrary.Playnite?.ApplicationSettings?.Language))
            {
                var byTitleId = Load(region);
                if (byTitleId == null)
                    continue;
                foreach (var kv in byTitleId)
                {
                    if (!merged.ContainsKey(kv.Key))
                        merged[kv.Key] = kv.Value;
                }
            }
            _byTitleId = merged;
        }

        // The preferred region first, then the rest, so a title's name/publisher/region come from the user's
        // most-likely region when it ships in several. English maps to USA (the titledb default region too).
        private static IEnumerable<string> RegionOrder(string playniteLanguage)
        {
            string preferred;
            switch (MetadataLanguage.TwoLetter(playniteLanguage))
            {
                case "ja": preferred = "JP"; break;
                case "ko": preferred = "KR"; break;
                case "zh": preferred = "TW"; break;
                case "en": preferred = "US"; break;
                default: preferred = "GB"; break; // other European languages
            }

            yield return preferred;
            foreach (var region in new[] { "US", "GB", "JP", "KR", "TW" })
            {
                if (!string.Equals(region, preferred, StringComparison.OrdinalIgnoreCase))
                    yield return region;
            }
        }

        // Returns null when the region file couldn't be made available or parsed (so the merge just skips it).
        private Dictionary<ulong, ExternalGameMetadata> Load(string region)
        {
            var logger = _emuLibrary.Logger;
            try
            {
                var localPath = Path.Combine(_emuLibrary.GetPluginUserDataPath(), "metadata", "3dsdb", "list_" + region + ".json");
                var url = string.Format(UrlFormat, region);
                var path = CachedRemoteFile.Ensure(localPath, MaxAge, tmp => CachedRemoteFile.Download(url, tmp, logger), logger);
                if (path == null)
                    return null;

                var label = RegionLabels.TryGetValue(region, out var l) ? l : null;
                using (var fs = File.OpenRead(path))
                {
                    var dict = ParseStream(fs, label);
                    logger?.Info($"[Metadata] 3dsdb \"{region}\" loaded with {dict.Count} titles.");
                    return dict;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[Metadata] Failed to load 3dsdb region \"{region}\".");
                return null;
            }
        }

        // Streams a region's JSON array, one normalized record per entry keyed by the parsed 16-hex "TitleID".
        // First entry per title id wins. The supplied regionLabel is attached to every record. Exposed
        // internally for unit testing.
        internal static Dictionary<ulong, ExternalGameMetadata> ParseStream(Stream stream, string regionLabel)
        {
            var result = new Dictionary<ulong, ExternalGameMetadata>();
            var serializer = new JsonSerializer();

            using (var sr = new StreamReader(stream))
            using (var jr = new JsonTextReader(sr))
            {
                while (jr.Read())
                {
                    if (jr.TokenType != JsonToken.StartObject)
                        continue;

                    var entry = serializer.Deserialize<ThreeDsDbEntry>(jr);
                    if (string.IsNullOrWhiteSpace(entry?.TitleId))
                        continue;
                    if (!ulong.TryParse(entry.TitleId.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var titleId))
                        continue;
                    if (result.ContainsKey(titleId))
                        continue;

                    result[titleId] = entry.ToExternal(regionLabel);
                }
            }
            return result;
        }

        private sealed class ThreeDsDbEntry
        {
            [JsonProperty("Name")] public string Name { get; set; }
            [JsonProperty("TitleID")] public string TitleId { get; set; }
            [JsonProperty("Publisher")] public string Publisher { get; set; }

            public ExternalGameMetadata ToExternal(string regionLabel) => new ExternalGameMetadata
            {
                Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
                Publishers = string.IsNullOrWhiteSpace(Publisher) ? null : new[] { Publisher.Trim() },
                Regions = string.IsNullOrWhiteSpace(regionLabel) ? null : new[] { regionLabel },
            };
        }
    }
}
