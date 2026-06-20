using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EmuLibrary.Util.Metadata
{
    // GameTDB metadata. GameTDB ships one zipped XML per platform (e.g. ps3tdb.zip -> ps3tdb.xml); each <game>
    // is keyed by the platform's serial/id. For PS3 that id is the disc serial (e.g. "BLES01234"), which
    // matches Ps3GameInfo.TitleId, so PS3 joins cleanly. The file is cached on disk, refreshed daily, and
    // fully fail-soft (empty database on any failure). GameTDB's Switch ids are cart serials that loose
    // NSP/XCI dumps don't carry, so Switch uses titledb instead, not this. The class itself is platform-
    // agnostic (parameterized by db name), ready to back other GameTDB platforms later.
    internal sealed class GameTdb
    {
        private const string UrlFormat = "https://www.gametdb.com/{0}.zip";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

        private readonly IEmuLibrary _emuLibrary;
        private readonly string _dbName; // e.g. "ps3tdb"
        private Dictionary<string, ExternalGameMetadata> _byId;

        public GameTdb(IEmuLibrary emuLibrary, string dbName)
        {
            _emuLibrary = emuLibrary;
            _dbName = dbName;
        }

        public bool TryGet(string id, out ExternalGameMetadata metadata)
        {
            EnsureLoaded();
            metadata = null;
            return !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim().ToUpperInvariant(), out metadata);
        }

        // Lazy so an empty/cancelled scan never triggers a download.
        private void EnsureLoaded()
        {
            if (_byId != null)
                return;

            var locale = MetadataLanguage.GameTdbLocale(_emuLibrary.Playnite?.ApplicationSettings?.Language);
            _byId = Load(locale) ?? new Dictionary<string, ExternalGameMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, ExternalGameMetadata> Load(string locale)
        {
            var logger = _emuLibrary.Logger;
            try
            {
                var localPath = Path.Combine(_emuLibrary.GetPluginUserDataPath(), "metadata", "gametdb", _dbName + ".xml");
                var url = string.Format(UrlFormat, _dbName);
                var path = CachedRemoteFile.Ensure(localPath, MaxAge,
                    tmp => DownloadAndExtract(url, _dbName + ".xml", tmp, logger), logger);
                if (path == null)
                    return null;

                using (var fs = File.OpenRead(path))
                {
                    var dict = ParseStream(fs, locale);
                    logger?.Info($"[Metadata] GameTDB \"{_dbName}\" loaded with {dict.Count} titles (locale {locale}).");
                    return dict;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[Metadata] Failed to load GameTDB \"{_dbName}\".");
                return null;
            }
        }

        // GameTDB serves the database as a zip; download it and extract the named XML entry into destPath.
        private static void DownloadAndExtract(string url, string xmlEntryName, string destPath, ILogger logger)
        {
            var zipTmp = destPath + ".zip";
            try
            {
                CachedRemoteFile.Download(url, zipTmp, logger);
                using (var archive = ZipFile.OpenRead(zipTmp))
                {
                    var entry = archive.GetEntry(xmlEntryName)
                        ?? archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        throw new FileNotFoundException($"No XML entry found in GameTDB archive from {url}.");
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }
            finally
            {
                try { if (File.Exists(zipTmp)) File.Delete(zipTmp); } catch { /* best effort cleanup */ }
            }
        }

        // Streams the GameTDB XML, one normalized record per <game> keyed by upper-cased <id>. The display
        // title prefers the requested locale, then English, then the game's name attribute. First id wins.
        // Exposed internally for unit testing.
        internal static Dictionary<string, ExternalGameMetadata> ParseStream(Stream stream, string locale)
        {
            var result = new Dictionary<string, ExternalGameMetadata>(StringComparer.OrdinalIgnoreCase);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };

            // GameTDB's XML occasionally embeds raw control characters (e.g. 0x1F) in a game's text. XmlReader
            // rejects those and would abort the whole parse, so strip them from the character stream before the
            // reader ever sees them (CheckCharacters=false is not sufficient — XNode.ReadFrom still throws).
            using (var sr = new StreamReader(stream))
            using (var sanitized = new XmlSanitizingTextReader(sr))
            using (var xr = XmlReader.Create(sanitized, settings))
            {
                xr.MoveToContent();
                while (!xr.EOF)
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.Name != "game")
                    {
                        xr.Read();
                        continue;
                    }

                    // Reads just this <game> subtree into an XElement and leaves the reader positioned on the
                    // following node, so the whole document is never materialized at once and the outer loop
                    // must not Read() again here (that would skip the next <game>).
                    var game = (XElement)XNode.ReadFrom(xr);
                    var id = (string)game.Element("id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    id = id.Trim().ToUpperInvariant();
                    if (result.ContainsKey(id))
                        continue;

                    result[id] = ToExternal(game, locale);
                }
            }
            return result;
        }

        private static ExternalGameMetadata ToExternal(XElement game, string locale) => new ExternalGameMetadata
        {
            Name = ResolveLocalized(game, locale, "title") ?? Clean((string)game.Attribute("name")),
            Description = ResolveLocalized(game, locale, "synopsis"),
            Developers = One((string)game.Element("developer")),
            Publishers = One((string)game.Element("publisher")),
            Genres = ParseGenres((string)game.Element("genre")),
            ReleaseDate = ParseDate(game.Element("date")),
        };

        // Value of <locale lang="X"><child/> preferring the requested locale, then English.
        private static string ResolveLocalized(XElement game, string locale, string child)
        {
            var value = (string)Locale(game, locale)?.Element(child)
                ?? (string)Locale(game, "EN")?.Element(child);
            return Clean(value);
        }

        private static XElement Locale(XElement game, string lang) =>
            game.Elements("locale").FirstOrDefault(l =>
                string.Equals((string)l.Attribute("lang"), lang, StringComparison.OrdinalIgnoreCase));

        private static IReadOnlyList<string> One(string value)
        {
            var v = Clean(value);
            return v == null ? null : new[] { v };
        }

        // GameTDB genres are a lowercase comma-separated list ("action,third-person shooter"); split and
        // title-case for presentation. Exposed internally for unit testing.
        internal static IReadOnlyList<string> ParseGenres(string genre)
        {
            if (string.IsNullOrWhiteSpace(genre))
                return null;

            var list = genre.Split(',')
                .Select(Clean)
                .Where(g => g != null)
                .Select(TitleCase)
                .ToList();
            return list.Count > 0 ? list : null;
        }

        // <date year="2006" month="11" day="11"/> with possibly-empty month/day. Exposed for unit testing.
        internal static ReleaseDate? ParseDate(XElement date)
        {
            if (date == null)
                return null;
            if (!int.TryParse((string)date.Attribute("year"), out var year) || year < 1970 || year > 3000)
                return null;

            bool hasMonth = int.TryParse((string)date.Attribute("month"), out var month) && month >= 1 && month <= 12;
            bool hasDay = int.TryParse((string)date.Attribute("day"), out var day) && day >= 1 && day <= 31;

            if (hasMonth && hasDay)
                return new ReleaseDate(year, month, day);
            if (hasMonth)
                return new ReleaseDate(year, month);
            return new ReleaseDate(year);
        }

        // Like Trim, but also drops control characters (GameTDB embeds stray ones like 0x1F). Returns null for
        // blank/empty results so callers leave the corresponding field unset.
        internal static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;

            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ')
                    sb.Append(ch);
            }

            var cleaned = sb.ToString().Trim();
            return cleaned.Length == 0 ? null : cleaned;
        }

        private static string TitleCase(string s) =>
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

        // Drops characters that are illegal in XML 1.0 from the underlying stream so a stray control char
        // (GameTDB embeds 0x1F) can't abort the parse. Filtering at the character-stream level is what makes
        // XmlReader tolerate them; XmlReaderSettings.CheckCharacters=false does not.
        private sealed class XmlSanitizingTextReader : TextReader
        {
            private readonly TextReader _inner;
            public XmlSanitizingTextReader(TextReader inner) { _inner = inner; }

            public override int Read(char[] buffer, int index, int count)
            {
                int written = 0;
                // Loop so a block that is entirely illegal chars doesn't return 0 (which signals EOF).
                while (written == 0)
                {
                    int read = _inner.Read(buffer, index, count);
                    if (read <= 0)
                        return read;

                    int w = index;
                    for (int r = index; r < index + read; r++)
                    {
                        if (IsLegalXmlChar(buffer[r]))
                            buffer[w++] = buffer[r];
                    }
                    written = w - index;
                }
                return written;
            }

            public override int Read()
            {
                int c;
                do { c = _inner.Read(); } while (c != -1 && !IsLegalXmlChar((char)c));
                return c;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }

            private static bool IsLegalXmlChar(char c) =>
                c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
        }
    }
}
