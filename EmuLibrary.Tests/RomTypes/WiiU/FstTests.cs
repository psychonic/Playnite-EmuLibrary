using EmuLibrary.RomTypes.WiiU.Crypto;
using System.Linq;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.WiiU
{
    // Pins the shared FST parser used by both NUS dumps and disc partitions: directory nesting, the
    // ContentFstInfo first section, and the file-offset convention (rawOffset * offsetFactor, except flag 0x4
    // means the raw value is already a byte offset).
    public class FstTests
    {
        [Fact]
        public void Parse_ReadsContentInfo_Tree_AndOffsetConventions()
        {
            // Entries: 0=root, 1=dir "meta" (subtree ends at 3), 2=file "meta/meta.xml", 3=file "boot.bin".
            const int totalEntries = 4;
            const int feBase = 0x40;             // 0x20 header + 1 content-info (0x20)
            const int nameBase = feBase + totalEntries * 0x10; // 0x80

            int size = nameBase + 23; // "meta\0meta.xml\0boot.bin\0"
            var fst = new byte[size];

            // Header
            fst[0] = 0x46; fst[1] = 0x53; fst[2] = 0x54; fst[3] = 0x00; // 'FST\0'
            PutBeU32(fst, 0x04, 0x20); // offsetFactor
            PutBeU32(fst, 0x08, 1);    // contentCount

            // ContentFstInfo[0] at 0x20
            PutBeU32(fst, 0x20, 2); // offsetSector -> Offset = 2*0x8000 - 0x8000 = 0x8000
            PutBeU32(fst, 0x24, 5); // sizeSector

            // FEntries (each 0x10): typeName, fileOffset/parent, fileSize/next, flags(u16), contentIndex(u16)
            PutBeU32(fst, feBase + 0 * 0x10 + 0, 0x01000000); // root: dir, name 0
            PutBeU32(fst, feBase + 0 * 0x10 + 8, totalEntries);

            PutBeU32(fst, feBase + 1 * 0x10 + 0, 0x01000000); // dir "meta", name offset 0
            PutBeU32(fst, feBase + 1 * 0x10 + 8, 3);          // subtree ends at index 3

            PutBeU32(fst, feBase + 2 * 0x10 + 0, 0x00000005); // file "meta.xml", name offset 5
            PutBeU32(fst, feBase + 2 * 0x10 + 4, 4);          // raw offset 4
            PutBeU32(fst, feBase + 2 * 0x10 + 8, 123);        // size
            // flags 0, contentIndex 0

            PutBeU32(fst, feBase + 3 * 0x10 + 0, 0x0000000E); // file "boot.bin", name offset 14
            PutBeU32(fst, feBase + 3 * 0x10 + 4, 7);          // raw offset 7
            PutBeU32(fst, feBase + 3 * 0x10 + 8, 10);         // size
            PutBeU16(fst, feBase + 3 * 0x10 + 0xC, 0x0004);   // flag 0x4 -> raw byte offset

            // Name section
            var names = Encoding.ASCII.GetBytes("meta\0meta.xml\0boot.bin\0");
            System.Buffer.BlockCopy(names, 0, fst, nameBase, names.Length);

            var parsed = Fst.Parse(fst);

            Assert.Equal(0x20, parsed.OffsetFactor);
            Assert.Single(parsed.Contents);
            Assert.Equal(0x8000, parsed.Contents[0].Offset);
            Assert.Equal(2, parsed.Files.Count);

            var meta = parsed.Find("meta/meta.xml");
            Assert.NotNull(meta);
            Assert.Equal(123u, meta.Size);
            Assert.Equal((ushort)0, meta.ContentIndex);
            Assert.Equal(0x80UL, meta.OffsetInContent); // 4 * 0x20

            var boot = parsed.Find("boot.bin");
            Assert.NotNull(boot);
            Assert.Equal(7UL, boot.OffsetInContent); // flag 0x4 -> raw, not multiplied
            Assert.Equal(10u, boot.Size);

            // Lookups are case-insensitive.
            Assert.NotNull(parsed.Find("META/META.XML"));
        }

        private static void PutBeU16(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        private static void PutBeU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
    }
}
