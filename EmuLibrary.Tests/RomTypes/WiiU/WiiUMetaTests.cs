using EmuLibrary.RomTypes.WiiU;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.WiiU
{
    // Guards Cemu.ParseMeta against the real-world failure that left every NUS title showing only its title id:
    // Wii U meta.xml is UTF-8 WITH a byte-order mark, and the decrypted NUS/.wua bytes are decoded via
    // Encoding.UTF8.GetString (which keeps the BOM as a leading U+FEFF). Without stripping it, XmlDocument
    // throws "Data at the root level is invalid. Line 1, position 1." and the name/product code are lost — which
    // also disables the GameTDB enrichment that keys on the product code.
    public class WiiUMetaTests
    {
        private const string MetaXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<menu type=\"complex\" access=\"777\">\n" +
            "  <longname_en>Gear  Gauntlet</longname_en>\n" +
            "  <product_code>WUP-N-ABCE</product_code>\n" +
            "  <title_id>00050000101e9300</title_id>\n" +
            "  <title_version>4</title_version>\n" +
            "</menu>";

        [Fact]
        public void ParseMeta_StripsUtf8Bom_FromDecodedBytes()
        {
            // Exactly what the NUS/.wua path produces: UTF-8 bytes WITH a BOM, decoded with GetString.
            var body = Encoding.UTF8.GetBytes(MetaXml);
            var bytes = new byte[3 + body.Length];
            bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
            System.Buffer.BlockCopy(body, 0, bytes, 3, body.Length);

            var text = Encoding.UTF8.GetString(bytes);

            var meta = Cemu.ParseMeta(text);

            Assert.NotNull(meta);
            Assert.Equal("Gear Gauntlet", meta.Name); // collapsed whitespace
            Assert.Equal("WUP-N-ABCE", meta.ProductCode);
            Assert.Equal(0x00050000101e9300UL, meta.TitleId);
            Assert.Equal(4u, meta.Version);
        }

        [Fact]
        public void ParseMeta_WorksWithoutBom_Too()
        {
            var meta = Cemu.ParseMeta(MetaXml);
            Assert.NotNull(meta);
            Assert.Equal("WUP-N-ABCE", meta.ProductCode);
        }

        // GameTDB's Wii U db is keyed by a 6-char id (game code + maker), NOT the raw product code. Passing the
        // raw "WUP-P-AVEE" matched nothing; BuildGameTdbId constructs the real key. The AVEE0W/Axiom Verge case
        // is verified against the live GameTDB Wii U database.
        [Theory]
        [InlineData("WUP-P-AVEE", "020W", "AVEE0W")]   // Axiom Verge (confirmed on gametdb.com/WiiU/AVEE0W)
        [InlineData("WUP-P-AENE", "017Z", "AENE7Z")]
        [InlineData("WUP-N-APKE", "0001", "APKE01")]   // Nintendo maker code 01
        [InlineData("wup-p-avee", "020w", "AVEE0W")]   // upper-cased
        public void BuildGameTdbId_MakesTheJoinKey(string product, string company, string expected)
        {
            Assert.Equal(expected, Cemu.BuildGameTdbId(product, company));
        }

        [Theory]
        [InlineData(null, "0001")]
        [InlineData("WUP-P-AVEE", null)]
        [InlineData("WUP-P-AVEE", "")]
        [InlineData("ABC", "01")]      // game code too short
        [InlineData("WUP-P-AVEE", "0")] // maker too short
        public void BuildGameTdbId_NullWhenIncomplete(string product, string company)
        {
            Assert.Null(Cemu.BuildGameTdbId(product, company));
        }

        // Many later retail discs ship a system-update partition (id 0005001010060000, a 00050010 "system
        // application" type) beside the game. Its masked base (0005000010060000) is a game-type id with no
        // meta.xml longname, so without filtering, every such disc collapses into one phantom game shown as
        // its title id. Only application/DLC/update title types are real library content.
        [Theory]
        [InlineData(0x0005000010102000UL, true)]  // application (game)
        [InlineData(0x0005000E10102000UL, true)]  // update / patch
        [InlineData(0x0005000C10102000UL, true)]  // DLC
        [InlineData(0x0005001010060000UL, false)] // on-disc system-update partition
        [InlineData(0x0005001B10000000UL, false)] // system data archive
        [InlineData(0x0005003010000000UL, false)] // applet
        public void IsContentTitleId_OnlyApplicationContent(ulong titleId, bool expected)
        {
            Assert.Equal(expected, Cemu.IsContentTitleId(titleId));
        }
    }
}
