using EmuLibrary.Util.Metadata;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace EmuLibrary.Tests.Util.Metadata
{
    public class GameTdbTests
    {
        // Mirrors the real ps3tdb.xml shape: <id> serial, localized <locale><title>/<synopsis>, single
        // <developer>/<publisher>, comma-separated lowercase <genre>, and a <date> with empty month/day.
        private const string Sample = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<datafile>
  <game name=""Fallback Name (EN)"">
    <id>blus30490</id>
    <locale lang=""EN""><title>English Title</title><synopsis>English synopsis.</synopsis></locale>
    <locale lang=""DE""><title>Deutscher Titel</title><synopsis>Deutsche Beschreibung.</synopsis></locale>
    <developer>BEC</developer>
    <publisher>Namco Bandai Games</publisher>
    <date year=""2006"" month=""11"" day=""11""/>
    <genre>action,third-person shooter</genre>
  </game>
  <game name=""Only English (EN)"">
    <id>BLES01234</id>
    <locale lang=""EN""><title>Only English Title</title></locale>
    <date year=""2014"" month="""" day=""""/>
  </game>
  <game name=""No Locale Game"">
    <id>BLES99999</id>
    <date year=""2010"" month="""" day=""""/>
  </game>
</datafile>";

        // Unit Separator (0x1F): a real invalid-XML control char GameTDB embeds in some ps3tdb.xml titles.
        // Built in code (not as a source literal) to keep this file pure ASCII.
        private static readonly char Ctrl = (char)0x1F;

        private static System.Collections.Generic.Dictionary<string, ExternalGameMetadata> Parse(string xml, string locale) =>
            GameTdb.ParseStream(new MemoryStream(Encoding.UTF8.GetBytes(xml)), locale);

        [Fact]
        public void ParseStream_KeysByUppercasedId()
        {
            var db = Parse(Sample, "EN");
            Assert.True(db.ContainsKey("BLUS30490")); // was lowercase in the file
            Assert.True(db.ContainsKey("BLES01234"));
        }

        [Fact]
        public void ParseStream_PrefersRequestedLocale()
        {
            var de = Parse(Sample, "DE")["BLUS30490"];
            Assert.Equal("Deutscher Titel", de.Name);
            Assert.Equal("Deutsche Beschreibung.", de.Description);
        }

        [Fact]
        public void ParseStream_FallsBackToEnglish_ThenToNameAttribute()
        {
            var db = Parse(Sample, "DE");
            // Only an EN locale exists -> use it even though DE was requested.
            Assert.Equal("Only English Title", db["BLES01234"].Name);
            // No locale at all -> fall back to the <game name> attribute.
            Assert.Equal("No Locale Game", db["BLES99999"].Name);
        }

        [Fact]
        public void ParseStream_ExtractsDeveloperPublisherAndGenres()
        {
            var g = Parse(Sample, "EN")["BLUS30490"];
            Assert.Equal("BEC", g.Developers.Single());
            Assert.Equal("Namco Bandai Games", g.Publishers.Single());
            Assert.Equal(new[] { "Action", "Third-Person Shooter" }, g.Genres.ToArray());
        }

        [Fact]
        public void ParseStream_ParsesFullDate()
        {
            var g = Parse(Sample, "EN")["BLUS30490"];
            Assert.Equal(2006, g.ReleaseDate.Value.Year);
            Assert.Equal(11, g.ReleaseDate.Value.Month);
            Assert.Equal(11, g.ReleaseDate.Value.Day);
        }

        [Fact]
        public void ParseDate_YearOnly_WhenMonthDayEmpty()
        {
            var rd = GameTdb.ParseDate(XElement.Parse(@"<date year=""2014"" month="""" day=""""/>"));
            Assert.NotNull(rd);
            Assert.Equal(2014, rd.Value.Year);
            Assert.Null(rd.Value.Month);
            Assert.Null(rd.Value.Day);
        }

        [Fact]
        public void ParseGenres_SplitsAndTitleCases()
        {
            var genres = GameTdb.ParseGenres("action, role-playing,strategy");
            Assert.Equal(new[] { "Action", "Role-Playing", "Strategy" }, genres.ToArray());
        }

        [Fact]
        public void ParseGenres_ReturnsNull_ForBlank()
        {
            Assert.Null(GameTdb.ParseGenres(""));
            Assert.Null(GameTdb.ParseGenres(null));
        }

        [Fact]
        public void ParseStream_SurvivesInvalidControlCharacters_AndScrubsThem()
        {
            // The whole parse must not abort on the embedded control char, and the char must be scrubbed.
            var xml = "<datafile><game name=\"X\"><id>BLES00001</id>" +
                      "<locale lang=\"EN\"><title>Hot" + Ctrl + "Line</title></locale></game>" +
                      "<game name=\"Y\"><id>BLES00002</id>" +
                      "<locale lang=\"EN\"><title>Second</title></locale></game></datafile>";

            var db = Parse(xml, "EN");

            // Both games parsed (the bad char didn't kill the run), and the control char is gone.
            Assert.Equal(2, db.Count);
            Assert.Equal("HotLine", db["BLES00001"].Name);
            Assert.Equal("Second", db["BLES00002"].Name);
        }

        [Fact]
        public void Clean_RemovesControlChars_KeepsTabsAndTrims()
        {
            Assert.Equal("ab", GameTdb.Clean("a" + Ctrl + "b"));
            Assert.Equal("a\tb", GameTdb.Clean("  a\tb  "));
            Assert.Null(GameTdb.Clean(Ctrl.ToString()));
            Assert.Null(GameTdb.Clean(""));
        }
    }
}
