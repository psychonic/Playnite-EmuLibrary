using EmuLibrary.Util.Metadata;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.Util.Metadata
{
    public class TitleDbTests
    {
        // Mirrors the real titledb shape: a top-level object keyed by nsuId, each entry carrying the 16-hex
        // title id in "id". Includes a duplicate title id (different nsuId) and a non-object value to exercise
        // dedup + the skip path.
        private const string Sample = @"{
            ""70010000000025"": {
                ""id"": ""01007EF00011E000"",
                ""name"": ""The Legend of Zelda: Breath of the Wild"",
                ""publisher"": ""Nintendo"",
                ""developer"": null,
                ""description"": ""Step into a world of adventure."",
                ""category"": [""Adventure"", ""Action""],
                ""releaseDate"": 20170303,
                ""iconUrl"": ""https://example/icon.jpg"",
                ""bannerUrl"": ""https://example/banner.jpg""
            },
            ""70010000099999"": {
                ""id"": ""01007EF00011E000"",
                ""name"": ""DUPLICATE - should be ignored"",
                ""publisher"": ""Someone Else""
            },
            ""schemaVersion"": 3,
            ""70010000000041"": {
                ""id"": ""0100000000010000"",
                ""name"": ""Super Mario Odyssey"",
                ""publisher"": ""Nintendo"",
                ""category"": [""Platformer""],
                ""releaseDate"": 0
            }
        }";

        private static System.Collections.Generic.Dictionary<ulong, ExternalGameMetadata> Parse(string json) =>
            TitleDb.ParseStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        [Fact]
        public void ParseStream_KeysByParsedHexTitleId_NotNsuId()
        {
            var db = Parse(Sample);
            Assert.True(db.ContainsKey(0x01007EF00011E000));
            Assert.True(db.ContainsKey(0x0100000000010000));
            Assert.False(db.ContainsKey(70010000000025)); // the nsuId is not a key
        }

        [Fact]
        public void ParseStream_ExtractsFields()
        {
            var zelda = Parse(Sample)[0x01007EF00011E000];
            Assert.Equal("The Legend of Zelda: Breath of the Wild", zelda.Name);
            Assert.Equal("Nintendo", zelda.Publishers.Single());
            Assert.Null(zelda.Developers); // developer was null
            Assert.Equal("Step into a world of adventure.", zelda.Description);
            Assert.Equal(new[] { "Adventure", "Action" }, zelda.Genres.ToArray());
            Assert.Equal(2017, zelda.ReleaseDate.Value.Year);
            Assert.Equal(3, zelda.ReleaseDate.Value.Month);
            Assert.Equal(3, zelda.ReleaseDate.Value.Day);
        }

        [Fact]
        public void ParseStream_FirstEntryWins_OnDuplicateTitleId()
        {
            var entry = Parse(Sample)[0x01007EF00011E000];
            Assert.Equal("The Legend of Zelda: Breath of the Wild", entry.Name);
            Assert.DoesNotContain("DUPLICATE", entry.Name);
        }

        [Fact]
        public void ParseStream_SkipsNonObjectTopLevelValues()
        {
            // "schemaVersion": 3 must not blow up parsing or add an entry.
            Assert.Equal(2, Parse(Sample).Count);
        }

        [Fact]
        public void ParseReleaseDate_DecodesFullDate()
        {
            var rd = TitleDb.ParseReleaseDate(20171027);
            Assert.NotNull(rd);
            Assert.Equal(2017, rd.Value.Year);
            Assert.Equal(10, rd.Value.Month);
            Assert.Equal(27, rd.Value.Day);
        }

        [Fact]
        public void ParseReleaseDate_KeepsMonth_WhenDayMissing()
        {
            var rd = TitleDb.ParseReleaseDate(20170300);
            Assert.NotNull(rd);
            Assert.Equal(2017, rd.Value.Year);
            Assert.Equal(3, rd.Value.Month);
            Assert.Null(rd.Value.Day);
        }

        [Fact]
        public void ParseReleaseDate_YearOnly_WhenMonthMissing()
        {
            var rd = TitleDb.ParseReleaseDate(20170000);
            Assert.NotNull(rd);
            Assert.Equal(2017, rd.Value.Year);
            Assert.Null(rd.Value.Month);
            Assert.Null(rd.Value.Day);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(null)]
        public void ParseReleaseDate_ReturnsNull_ForMissing(long? input)
        {
            Assert.Null(TitleDb.ParseReleaseDate(input));
        }
    }
}
