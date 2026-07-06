using EmuLibrary.RomTypes.Xbox360;
using System.Collections.Generic;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Xbox360
{
    public class XdbfTests
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };

        private sealed class Entry
        {
            public ushort Namespace;
            public ulong Id;
            public byte[] Data;
        }

        // Builds a synthetic XDBF: header, entry table (18 bytes/entry), an empty free table, then the data
        // region entries reference by offset. Mirrors the on-disk SPA/GPD layout the parser walks.
        private static byte[] Build(IEnumerable<Entry> entries)
        {
            var list = new List<Entry>(entries);
            int entryTableLength = list.Count;
            int dataRegion = 0x18 + entryTableLength * 18; // no free-table slots

            var head = new List<byte>();
            head.AddRange(new byte[] { (byte)'X', (byte)'D', (byte)'B', (byte)'F' });
            AddU32BE(head, 0x10000);                // version
            AddU32BE(head, (uint)entryTableLength); // entry table length
            AddU32BE(head, (uint)entryTableLength); // entry count
            AddU32BE(head, 0);                      // free table length
            AddU32BE(head, 0);                      // free count

            var data = new List<byte>();
            foreach (var e in list)
            {
                uint offset = (uint)data.Count;
                AddU16BE(head, e.Namespace);
                AddU64BE(head, e.Id);
                AddU32BE(head, offset);
                AddU32BE(head, (uint)e.Data.Length);
                data.AddRange(e.Data);
            }

            head.AddRange(data);
            Assert.Equal(dataRegion, 0x18 + entryTableLength * 18);
            return head.ToArray();
        }

        [Fact]
        public void FindsTitleImage()
        {
            var xdbf = Build(new[]
            {
                new Entry { Namespace = 1, Id = 0x8000, Data = new byte[] { 0xAA } }, // metadata decoy (same id, other ns)
                new Entry { Namespace = 2, Id = 0x8000, Data = Png },                 // the title image
                new Entry { Namespace = 2, Id = 0x8001, Data = new byte[] { 0xBB } }, // another image
            });

            Assert.True(Xdbf.TryReadTitleImage(xdbf, out var png));
            Assert.Equal(Png, png);
        }

        [Fact]
        public void RejectsWhenTitleImageMissing()
        {
            var xdbf = Build(new[]
            {
                new Entry { Namespace = 2, Id = 0x8001, Data = Png },
            });
            Assert.False(Xdbf.TryReadTitleImage(xdbf, out _));
        }

        [Fact]
        public void RejectsWhenImageIsNotPng()
        {
            var xdbf = Build(new[]
            {
                new Entry { Namespace = 2, Id = 0x8000, Data = new byte[] { 0x00, 0x01, 0x02, 0x03 } },
            });
            Assert.False(Xdbf.TryReadTitleImage(xdbf, out _));
        }

        [Fact]
        public void RejectsBadMagic()
        {
            var xdbf = Build(new[] { new Entry { Namespace = 2, Id = 0x8000, Data = Png } });
            xdbf[0] = (byte)'N';
            Assert.False(Xdbf.TryReadTitleImage(xdbf, out _));
        }

        [Fact]
        public void RejectsTruncatedTable()
        {
            var xdbf = Build(new[] { new Entry { Namespace = 2, Id = 0x8000, Data = Png } });
            // Claim more entries than the buffer can hold.
            xdbf[0x0B] = 0xFF;
            Assert.False(Xdbf.TryReadTitleImage(xdbf, out _));
        }

        [Fact]
        public void RejectsNullAndShort()
        {
            Assert.False(Xdbf.TryReadTitleImage(null, out _));
            Assert.False(Xdbf.TryReadTitleImage(new byte[] { (byte)'X', (byte)'D' }, out _));
        }

        private static void AddU16BE(List<byte> b, ushort v)
        {
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }

        private static void AddU32BE(List<byte> b, uint v)
        {
            b.Add((byte)(v >> 24));
            b.Add((byte)(v >> 16));
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }

        private static void AddU64BE(List<byte> b, ulong v)
        {
            AddU32BE(b, (uint)(v >> 32));
            AddU32BE(b, (uint)v);
        }
    }
}
