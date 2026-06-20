using Playnite.SDK.Models;
using System.Collections.Generic;
using System.Linq;

namespace EmuLibrary.Util.Metadata
{
    // Normalized, source-agnostic game metadata resolved from an external database (titledb for Switch,
    // GameTDB for PS3). Every field is optional; absent fields are left unset so they never clobber whatever
    // the scanner already derived from the game files. Image URL fields are parsed for future use but are not
    // applied yet (text-only for now).
    internal sealed class ExternalGameMetadata
    {
        public string Name;
        public string Description;
        public IReadOnlyList<string> Developers;
        public IReadOnlyList<string> Publishers;
        public IReadOnlyList<string> Genres;
        public ReleaseDate? ReleaseDate;

        // Parsed for completeness/future use; intentionally not applied to GameMetadata yet.
        public string IconUrl;
        public string CoverUrl;
        public string BackgroundUrl;

        // Combines candidates in priority order (highest priority first): the first non-empty value per field
        // wins. Lets a platform stack additional sources later without touching callers (e.g. titledb over
        // GameTDB). Today each platform has a single remote source, but the seam keeps "shadowing" uniform.
        public static ExternalGameMetadata Coalesce(params ExternalGameMetadata[] sourcesHighToLow)
        {
            var result = new ExternalGameMetadata();
            foreach (var s in sourcesHighToLow)
            {
                if (s == null)
                    continue;
                if (IsBlank(result.Name)) result.Name = NullIfBlank(s.Name);
                if (IsBlank(result.Description)) result.Description = NullIfBlank(s.Description);
                if (IsEmpty(result.Developers)) result.Developers = NullIfEmpty(s.Developers);
                if (IsEmpty(result.Publishers)) result.Publishers = NullIfEmpty(s.Publishers);
                if (IsEmpty(result.Genres)) result.Genres = NullIfEmpty(s.Genres);
                if (result.ReleaseDate == null) result.ReleaseDate = s.ReleaseDate;
                if (IsBlank(result.IconUrl)) result.IconUrl = NullIfBlank(s.IconUrl);
                if (IsBlank(result.CoverUrl)) result.CoverUrl = NullIfBlank(s.CoverUrl);
                if (IsBlank(result.BackgroundUrl)) result.BackgroundUrl = NullIfBlank(s.BackgroundUrl);
            }
            return result;
        }

        // Overlays onto scanner-built metadata. External values shadow the file-derived ones where present;
        // fields the files don't provide (description/developers/publishers/genres/release date) are filled in.
        public void ApplyTo(GameMetadata game)
        {
            if (!IsBlank(Name))
                game.Name = Name;
            if (!IsBlank(Description))
                game.Description = Description;
            if (!IsEmpty(Developers))
                game.Developers = ToProperties(Developers);
            if (!IsEmpty(Publishers))
                game.Publishers = ToProperties(Publishers);
            if (!IsEmpty(Genres))
                game.Genres = ToProperties(Genres);
            if (ReleaseDate != null)
                game.ReleaseDate = ReleaseDate;
        }

        private static HashSet<MetadataProperty> ToProperties(IReadOnlyList<string> values) =>
            new HashSet<MetadataProperty>(values.Where(v => !IsBlank(v))
                                                .Select(v => (MetadataProperty)new MetadataNameProperty(v.Trim())));

        private static bool IsBlank(string s) => string.IsNullOrWhiteSpace(s);
        private static string NullIfBlank(string s) => IsBlank(s) ? null : s;
        private static bool IsEmpty(IReadOnlyList<string> l) => l == null || l.Count == 0;
        private static IReadOnlyList<string> NullIfEmpty(IReadOnlyList<string> l) => IsEmpty(l) ? null : l;
    }
}
