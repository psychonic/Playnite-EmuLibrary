using EmuLibrary.Util.Metadata;
using System.IO;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.Util.Metadata
{
    public class Xbox360DbTests
    {
        // Mirrors the real xenia-manager/x360db games.json: a top-level array of objects keyed by the 8-hex
        // "id", with a "title" name and a "boxart" cover URL. Includes a leading-space title (the DB has these),
        // a duplicate id, and a non-hex id to exercise trimming, dedup, and the skip path.
        private const string Sample = @"[
            { ""id"": ""5841098F"", ""alternative_id"": [], ""title"": "" 'Splosion Man"", ""boxart"": ""http://x/box.jpg"", ""media_id"": [] },
            { ""id"": ""45410869"", ""title"": ""Rock Band 2"" },
            { ""id"": ""5841098F"", ""title"": ""DUPLICATE - ignored"" },
            { ""id"": ""not-hex"", ""title"": ""Bad Id"" }
        ]";

        private static System.Collections.Generic.Dictionary<uint, ExternalGameMetadata> Parse(string json) =>
            Xbox360Db.ParseStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        [Fact]
        public void ParseStream_KeysByHexTitleId_AndTrimsName()
        {
            var db = Parse(Sample);
            Assert.True(db.ContainsKey(0x5841098F));
            Assert.Equal("'Splosion Man", db[0x5841098F].Name); // leading space trimmed
            Assert.Equal("http://x/box.jpg", db[0x5841098F].CoverUrl);
        }

        [Fact]
        public void ParseStream_FirstEntryWins_OnDuplicateId()
        {
            Assert.Equal("'Splosion Man", Parse(Sample)[0x5841098F].Name);
        }

        [Fact]
        public void ParseStream_SkipsNonHexIds()
        {
            // Two valid entries (the duplicate and the bad-id rows don't add keys).
            Assert.Equal(2, Parse(Sample).Count);
            Assert.True(Parse(Sample).ContainsKey(0x45410869));
        }
    }
}
