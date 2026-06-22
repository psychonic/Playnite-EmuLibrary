using EmuLibrary.RomTypes.WiiU;
using EmuLibrary.RomTypes.WiiU.Crypto;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.WiiU
{
    // Pins the fixed-offset TMD parse (title id / version / content classification) against synthetic TMDs,
    // and the content-kind derivation from the title-id high word.
    public class TitleMetadataTests
    {
        private const string RetailIssuer = "Root-CA00000003-CP0000000b";

        // Builds a minimal but structurally valid Wii U TMD with one content entry.
        private static byte[] BuildTmd(ulong titleId, ushort titleVersion, ushort contentType, ulong contentSize)
        {
            var buf = new byte[0xB04 + 0x30];

            buf[0x180] = 1; // Version

            var issuer = Encoding.ASCII.GetBytes(RetailIssuer);
            System.Array.Copy(issuer, 0, buf, 0x140, issuer.Length);

            PutU64(buf, 0x18C, titleId);
            PutU16(buf, 0x1DC, titleVersion);
            PutU16(buf, 0x1DE, 1); // ContentCount

            // Content[0] at 0xB04: ID(u32), Index(u16), Type(u16), Size(u64)
            PutU32(buf, 0xB04 + 0, 0x12345678);
            PutU16(buf, 0xB04 + 4, 0);
            PutU16(buf, 0xB04 + 6, contentType);
            PutU64(buf, 0xB04 + 8, contentSize);

            return buf;
        }

        [Fact]
        public void Parse_ReadsCoreFields()
        {
            var tmd = TitleMetadata.Parse(BuildTmd(0x0005000010102000UL, 16, 0x2003, 0x8000));

            Assert.Equal(0x0005000010102000UL, tmd.TitleId);
            Assert.Equal((ushort)16, tmd.TitleVersion);
            Assert.False(tmd.IsDev);
            Assert.Equal(WiiUContentKind.Game, tmd.Kind);

            var c = Assert.Single(tmd.Contents);
            Assert.Equal(0x12345678u, c.Id);
            Assert.Equal(0x8000UL, c.Size);
            Assert.True(c.IsHashed); // Type 0x2003 has the 0x02 hash bit set
        }

        [Fact]
        public void Plain_Content_IsNotHashed()
        {
            var tmd = TitleMetadata.Parse(BuildTmd(0x0005000010102000UL, 1, 0x0001, 0x40));
            Assert.False(Assert.Single(tmd.Contents).IsHashed);
        }

        // expectedKind is the int value of WiiUContentKind (Game=0, Update=1, Dlc=2); the enum is internal so
        // it can't appear in this public Theory's signature.
        [Theory]
        [InlineData(0x0005000010102000UL, 0)]
        [InlineData(0x0005000E10102000UL, 1)]
        [InlineData(0x0005000C10102000UL, 2)]
        public void Kind_DerivedFromTitleIdHighWord(ulong titleId, int expectedKind)
        {
            var tmd = TitleMetadata.Parse(BuildTmd(titleId, 1, 0x0001, 0x40));
            Assert.Equal((WiiUContentKind)expectedKind, tmd.Kind);
            // All three share a base title id (high-word content-type bits masked out).
            Assert.Equal(0x0005000010102000UL, tmd.BaseTitleId);
        }

        private static void PutU16(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        private static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
        private static void PutU64(byte[] b, int o, ulong v)
        {
            PutU32(b, o, (uint)(v >> 32)); PutU32(b, o + 4, (uint)v);
        }
    }
}
