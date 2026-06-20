using System;
using System.Collections.Generic;

namespace EmuLibrary.Util.Metadata
{
    // Maps Playnite's configured UI language (e.g. "en_US", "de_DE", "pt_BR") to each source's language
    // selector, always with an English fallback.
    internal static class MetadataLanguage
    {
        // titledb publishes per-region files named REGION.lang.json; pick a representative file per language.
        private static readonly Dictionary<string, string> TitleDbFileByLang =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = "US.en",
                ["ja"] = "JP.ja",
                ["de"] = "DE.de",
                ["fr"] = "FR.fr",
                ["es"] = "ES.es",
                ["it"] = "IT.it",
                ["nl"] = "NL.nl",
                ["pt"] = "PT.pt",
                ["ru"] = "RU.ru",
                ["ko"] = "KR.ko",
                ["zh"] = "HK.zh",
            };

        public const string DefaultTitleDbFile = "US.en";

        // Two-letter, lowercase language code from a Playnite language id like "en_US" / "pt-BR".
        public static string TwoLetter(string playniteLanguage)
        {
            if (string.IsNullOrWhiteSpace(playniteLanguage))
                return "en";
            var head = playniteLanguage.Trim().Replace('-', '_').Split('_')[0].ToLowerInvariant();
            return head.Length >= 2 ? head.Substring(0, 2) : "en";
        }

        // titledb file basename (without extension) for a Playnite language, defaulting to US.en.
        public static string TitleDbFile(string playniteLanguage) =>
            TitleDbFileByLang.TryGetValue(TwoLetter(playniteLanguage), out var file) ? file : DefaultTitleDbFile;

        // GameTDB <locale lang="XX"> codes are uppercase two-letter (EN, JA, DE, ...).
        public static string GameTdbLocale(string playniteLanguage) => TwoLetter(playniteLanguage).ToUpperInvariant();
    }
}
