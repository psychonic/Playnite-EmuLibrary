using EmuLibrary.Util.Metadata;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.Util.Metadata
{
    public class ThreeDsDbTests
    {
        // Mirrors the real hax0kartik/3dsdb shape: a JSON array of objects, each keyed by a 16-hex "TitleID".
        // Includes a duplicate title id and an entry missing TitleID to exercise dedup + the skip path.
        private const string Sample = @"[
            {
                ""Name"": ""Super Mario 3D Land"",
                ""UID"": ""50010000037588"",
                ""TitleID"": ""0004000000054100"",
                ""Version"": ""N/A"",
                ""Size"": ""128.0 MB [1024 blocks]"",
                ""Product Code"": ""CTR-P-AREP"",
                ""Publisher"": ""Nintendo""
            },
            {
                ""Name"": ""DUPLICATE - should be ignored"",
                ""TitleID"": ""0004000000054100"",
                ""Publisher"": ""Someone Else""
            },
            {
                ""Name"": ""No Title Id Here"",
                ""Publisher"": ""Nobody""
            },
            {
                ""Name"": ""The Legend of Zelda: Ocarina of Time 3D"",
                ""TitleID"": ""0004000000033500"",
                ""Publisher"": ""Nintendo""
            }
        ]";

        private static System.Collections.Generic.Dictionary<ulong, ExternalGameMetadata> Parse(string json, string region = "USA") =>
            ThreeDsDb.ParseStream(new MemoryStream(Encoding.UTF8.GetBytes(json)), region);

        [Fact]
        public void ParseStream_KeysByParsedHexTitleId()
        {
            var db = Parse(Sample);
            Assert.True(db.ContainsKey(0x0004000000054100));
            Assert.True(db.ContainsKey(0x0004000000033500));
            Assert.Equal(2, db.Count); // the entry with no TitleID is skipped
        }

        [Fact]
        public void ParseStream_ExtractsFields_AndAttachesRegion()
        {
            var mario = Parse(Sample, "USA")[0x0004000000054100];
            Assert.Equal("Super Mario 3D Land", mario.Name);
            Assert.Equal("Nintendo", mario.Publishers.Single());
            Assert.Equal("USA", mario.Regions.Single());
        }

        [Fact]
        public void ParseStream_FirstEntryWins_OnDuplicateTitleId()
        {
            var entry = Parse(Sample)[0x0004000000054100];
            Assert.Equal("Super Mario 3D Land", entry.Name);
            Assert.DoesNotContain("DUPLICATE", entry.Name);
        }

        [Fact]
        public void ParseStream_NoRegion_LeavesRegionsUnset()
        {
            var entry = Parse(Sample, null)[0x0004000000054100];
            Assert.Null(entry.Regions);
        }
    }
}
