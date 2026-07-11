using EmuLibrary.Util.Cso;
using EmuLibrary.Util.Psp;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;
using ZstdSharp;

namespace EmuLibrary.Tests.Util
{
    // End-to-end coverage for the PSP image-reader layer: a minimal ISO9660 with /PSP_GAME/PARAM.SFO is
    // wrapped in each supported container (plain ISO, CHD v5 none/zstd/zlib, CISO, ZISO, PBP) and the DISC_ID
    // is read back through PspImageReader. The CHD/CSO encoders here are independent implementations of the
    // container formats (own MSB-first bit writer + canonical Huffman for the CHD map), so they cross-check
    // the ported decoders rather than sharing code with them.
    public class PspImageReaderTests
    {
        private const string DiscId = "ULUS10538";

        [Fact]
        public void PlainIso_ReadsDiscId()
        {
            RunFileTest(".iso", PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)), DiscId);
        }

        [Fact]
        public void ChdNone_ReadsDiscId()
        {
            RunFileTest(".chd", BuildChd(PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)), 4096, ChdCodec.None), DiscId);
        }

        [Fact]
        public void ChdZstd_ReadsDiscId()
        {
            RunFileTest(".chd", BuildChd(PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)), 4096, ChdCodec.Zstd), DiscId);
        }

        [Fact]
        public void ChdZlib_ReadsDiscId()
        {
            RunFileTest(".chd", BuildChd(PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)), 4096, ChdCodec.Zlib), DiscId);
        }

        [Fact]
        public void ChdLzma_ReadsDiscId()
        {
            RunFileTest(".chd", BuildChd(PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)), 4096, ChdCodec.Lzma), DiscId);
        }

        [Fact]
        public void Ciso_ReadsDiscId()
        {
            RunFileTest(".cso", BuildCso(PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)), 2048, ziso: false), DiscId);
        }

        [Fact]
        public void Ziso_ReadsDiscId_PlainBlocks()
        {
            // ZISO container with all blocks stored uncompressed (plain-flag) — exercises the container/index
            // path without needing an LZ4 encoder; the LZ4 decode path is covered by Lz4_DecodesLiteralsAndMatch.
            RunFileTest(".zso", BuildCso(PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)), 2048, ziso: true, forcePlain: true), DiscId);
        }

        [Fact]
        public void Pbp_ReadsTitleId()
        {
            // A PBP has no DISC_ID; the reader falls back to TITLE_ID.
            RunFileTest(".pbp", BuildPbp(PspTestImage.BuildSfo(DiscId, titleIdOnly: true)), DiscId);
        }

        [Fact]
        public void PlainIso_ReadsIcon()
        {
            var icon = PspTestImage.FakePng();
            RunIconTest(".iso", PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId), icon), icon);
        }

        [Fact]
        public void ChdZstd_ReadsIcon()
        {
            var icon = PspTestImage.FakePng();
            RunIconTest(".chd", BuildChd(PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId), icon), 4096, ChdCodec.Zstd), icon);
        }

        [Fact]
        public void Ciso_ReadsIcon()
        {
            var icon = PspTestImage.FakePng();
            RunIconTest(".cso", BuildCso(PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId), icon), 2048, ziso: false), icon);
        }

        [Fact]
        public void Pbp_ReadsIcon()
        {
            // ICON0.PNG is the PBP's second segment.
            var icon = PspTestImage.FakePng();
            RunIconTest(".pbp", BuildPbp(PspTestImage.BuildSfo(DiscId, titleIdOnly: true), icon), icon);
        }

        [Fact]
        public void Iso_NoIcon_ReturnsFalse()
        {
            var path = TempPath(".iso");
            try
            {
                // No ICON0.PNG present -> TryReadIcon reports false rather than returning garbage.
                File.WriteAllBytes(path, PspTestImage.BuildPspIso(PspTestImage.BuildSfo(DiscId)));
                Assert.False(PspImageReader.TryReadIcon(path, out _));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void UnsupportedExtension_ReturnsFalse()
        {
            var path = TempPath(".bin");
            try
            {
                File.WriteAllBytes(path, new byte[64]);
                Assert.False(PspImageReader.TryReadTitleId(path, out _));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Lz4_DecodesLiteralsAndMatch()
        {
            // "ABABABAB..." : 2 literal bytes then a match (offset 2) copying the rest. Exercises the LZ4
            // literal copy, the 2-byte little-endian offset, and the overlapping match copy.
            var expected = new byte[16];
            for (int i = 0; i < expected.Length; i++)
                expected[i] = (byte)(i % 2 == 0 ? 'A' : 'B');

            // token: 2 literals (high nibble) + matchlen 16-4=... encode matchLen field = 14-4? matchLen field
            // holds (len-4); we want 14 copied bytes => field 10.
            var block = new byte[] {
                (0x2 << 4) | 0x0A, // 2 literals, match length field 10 (=> 14 bytes)
                (byte)'A', (byte)'B',
                0x02, 0x00,        // offset = 2 (little-endian)
            };

            var got = Lz4.DecompressBlock(block, expected.Length);
            Assert.Equal(expected, got);
        }

        private static void RunFileTest(string ext, byte[] bytes, string expectedId)
        {
            var path = TempPath(ext);
            try
            {
                File.WriteAllBytes(path, bytes);
                Assert.True(PspImageReader.TryReadTitleId(path, out var id), $"{ext}: title id not read");
                Assert.Equal(expectedId, id);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void RunIconTest(string ext, byte[] bytes, byte[] expectedIcon)
        {
            var path = TempPath(ext);
            try
            {
                File.WriteAllBytes(path, bytes);
                Assert.True(PspImageReader.TryReadIcon(path, out var icon), $"{ext}: icon not read");
                Assert.Equal(expectedIcon, icon);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static string TempPath(string ext) =>
            Path.Combine(Path.GetTempPath(), "eltest-psp-" + Guid.NewGuid().ToString("N") + ext);

        #region PBP builder

        private static byte[] BuildPbp(byte[] sfoBytes, byte[] icon0 = null)
        {
            // 40-byte header + PARAM.SFO segment, optionally followed by an ICON0.PNG segment; the remaining
            // segment offsets all point at the end (empty). ICON0.PNG spans [offset[1], offset[2]).
            int sfoOffset = 40;
            int icon0Offset = sfoOffset + sfoBytes.Length;
            int end = icon0Offset + (icon0?.Length ?? 0);
            var buf = new byte[end];
            buf[0] = 0x00; buf[1] = (byte)'P'; buf[2] = (byte)'B'; buf[3] = (byte)'P';
            WriteU32LE(buf, 0x04, 0x00010000); // version
            WriteU32LE(buf, 0x08, (uint)sfoOffset);   // PARAM.SFO
            WriteU32LE(buf, 0x0C, (uint)icon0Offset); // ICON0.PNG (== end when absent => empty segment)
            for (int o = 0x10; o <= 0x24; o += 4)
                WriteU32LE(buf, o, (uint)end); // all later segments empty
            Buffer.BlockCopy(sfoBytes, 0, buf, sfoOffset, sfoBytes.Length);
            if (icon0 != null)
                Buffer.BlockCopy(icon0, 0, buf, icon0Offset, icon0.Length);
            return buf;
        }

        #endregion

        #region CSO / ZISO builder

        private static byte[] BuildCso(byte[] logical, int blockSize, bool ziso, bool forcePlain = false)
        {
            int numBlocks = (logical.Length + blockSize - 1) / blockSize;
            const int align = 0;

            var blocks = new List<byte[]>();
            var plainFlags = new List<bool>();
            for (int i = 0; i < numBlocks; i++)
            {
                var plain = new byte[blockSize];
                int copy = Math.Min(blockSize, logical.Length - i * blockSize);
                Buffer.BlockCopy(logical, i * blockSize, plain, 0, copy);

                if (forcePlain)
                {
                    blocks.Add(plain);
                    plainFlags.Add(true);
                }
                else if (!ziso)
                {
                    // CISO: raw DEFLATE per block.
                    using (var ms = new MemoryStream())
                    {
                        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                            ds.Write(plain, 0, plain.Length);
                        var comp = ms.ToArray();
                        if (comp.Length >= blockSize) { blocks.Add(plain); plainFlags.Add(true); }
                        else { blocks.Add(comp); plainFlags.Add(false); }
                    }
                }
                else
                {
                    blocks.Add(plain);
                    plainFlags.Add(true);
                }
            }

            int headerSize = 24;
            int indexSize = (numBlocks + 1) * 4;
            int dataStart = headerSize + indexSize;

            var index = new uint[numBlocks + 1];
            int cursor = dataStart;
            for (int i = 0; i < numBlocks; i++)
            {
                index[i] = (uint)(cursor >> align);
                if (plainFlags[i]) index[i] |= 0x80000000u;
                cursor += blocks[i].Length;
            }
            index[numBlocks] = (uint)(cursor >> align);

            var outp = new byte[cursor];
            uint magic = ziso ? 0x4F53495Au : 0x4F534943u; // "ZISO" / "CISO"
            WriteU32LE(outp, 0, magic);
            WriteU32LE(outp, 4, (uint)headerSize);
            WriteU64LE(outp, 8, (ulong)logical.Length);
            WriteU32LE(outp, 16, (uint)blockSize);
            outp[20] = ziso ? (byte)1 : (byte)1; // version
            outp[21] = align;
            for (int i = 0; i <= numBlocks; i++)
                WriteU32LE(outp, headerSize + i * 4, index[i]);
            int p = dataStart;
            foreach (var b in blocks)
            {
                Buffer.BlockCopy(b, 0, outp, p, b.Length);
                p += b.Length;
            }
            return outp;
        }

        #endregion

        #region CHD v5 builder

        private enum ChdCodec { None, Zstd, Zlib, Lzma }

        private static byte[] BuildChd(byte[] logical, int hunkBytes, ChdCodec codec)
        {
            int unitBytes = 2048;
            int hunkCount = (logical.Length + hunkBytes - 1) / hunkBytes;

            // Compress/collect each hunk payload.
            var payloads = new List<byte[]>();
            for (int h = 0; h < hunkCount; h++)
            {
                var plain = new byte[hunkBytes];
                int copy = Math.Min(hunkBytes, logical.Length - h * hunkBytes);
                if (copy > 0)
                    Buffer.BlockCopy(logical, h * hunkBytes, plain, 0, copy);

                switch (codec)
                {
                    case ChdCodec.None:
                        payloads.Add(plain);
                        break;
                    case ChdCodec.Zstd:
                        using (var c = new Compressor(3))
                            payloads.Add(c.Wrap(plain).ToArray());
                        break;
                    case ChdCodec.Zlib:
                        using (var ms = new MemoryStream())
                        {
                            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                                ds.Write(plain, 0, plain.Length);
                            payloads.Add(ms.ToArray());
                        }
                        break;
                    case ChdCodec.Lzma:
                        payloads.Add(LzmaCompressHunk(plain, hunkBytes));
                        break;
                }
            }

            byte compType = codec == ChdCodec.None ? (byte)4 /*NONE*/ : (byte)0 /*TYPE_0*/;
            const int lengthBits = 24;

            // --- Build the compressed map body (independent MSB-first bit writer + canonical Huffman). ---
            var bw = new BitWriter();

            // Single-symbol Huffman tree (import_tree_rle, numbits=4 for maxbits=8): symbol `compType`
            // has code length 1, all others length 0.
            for (int node = 0; node < 16; node++)
            {
                if (node == compType)
                {
                    bw.Write(1, 4); // escape
                    bw.Write(1, 4); // "double 1" => length 1
                }
                else
                {
                    bw.Write(0, 4); // length 0
                }
            }

            // Type codes: the single symbol is canonical code 0, length 1 => one 0 bit per hunk.
            for (int h = 0; h < hunkCount; h++)
                bw.Write(0, 1);

            // Second pass fields, per hunk in order.
            for (int h = 0; h < hunkCount; h++)
            {
                if (codec != ChdCodec.None)
                    bw.Write((uint)payloads[h].Length, lengthBits);
                bw.Write(0, 16); // crc16 (ignored by the reader)
            }

            var mapBody = bw.ToArray();
            int mapBytes = mapBody.Length;

            const int headerLen = 124;
            const int mapOffset = headerLen;
            long firstOffs = headerLen + 16 + mapBytes;

            long totalHunkBytes = 0;
            foreach (var pl in payloads) totalHunkBytes += pl.Length;
            var outp = new byte[firstOffs + totalHunkBytes];

            // --- Header ---
            Encoding.ASCII.GetBytes("MComprHD").CopyTo(outp, 0);
            WriteU32BE(outp, 8, headerLen);
            WriteU32BE(outp, 12, 5); // version
            uint fourcc =
                codec == ChdCodec.Zlib ? FourCc("zlib") :
                codec == ChdCodec.Lzma ? FourCc("lzma") : FourCc("zstd");
            WriteU32BE(outp, 16, fourcc); // compressors[0] (nonzero => compressed CHD)
            WriteU64BE(outp, 32, (ulong)logical.Length);
            WriteU64BE(outp, 40, mapOffset);
            WriteU64BE(outp, 48, 0); // metaoffset
            WriteU32BE(outp, 56, (uint)hunkBytes);
            WriteU32BE(outp, 60, (uint)unitBytes);

            // --- Map header (16 bytes) ---
            WriteU32BE(outp, mapOffset + 0, (uint)mapBytes);
            WriteU48BE(outp, mapOffset + 4, (ulong)firstOffs);
            WriteU16BE(outp, mapOffset + 10, 0); // mapcrc (ignored)
            outp[mapOffset + 12] = lengthBits;
            outp[mapOffset + 13] = 0; // selfbits
            outp[mapOffset + 14] = 0; // parentbits
            outp[mapOffset + 15] = 0;

            // --- Map body ---
            Buffer.BlockCopy(mapBody, 0, outp, mapOffset + 16, mapBytes);

            // --- Hunk payloads, contiguous from firstOffs ---
            long p = firstOffs;
            foreach (var pl in payloads)
            {
                Buffer.BlockCopy(pl, 0, outp, (int)p, pl.Length);
                p += pl.Length;
            }

            return outp;
        }

        // Produces a raw LZMA1 stream for one hunk, matching how chdman stores lzma hunks: no props/size
        // header (props are derived from the hunk size on decode), default lc/lp/pb (3/0/2), no end marker,
        // and a dictionary size that mirrors LzmaEncProps_Normalize for reduceSize = hunkBytes so the encoder's
        // match distances fit the decoder's reconstructed window.
        private static byte[] LzmaCompressHunk(byte[] plain, int hunkBytes)
        {
            uint dictSize = 1u << 26;
            if (hunkBytes > 0 && dictSize > (uint)hunkBytes)
            {
                uint reduce = (uint)hunkBytes;
                for (int i = 11; i <= 30; i++)
                {
                    if (reduce <= (2u << i)) { dictSize = 2u << i; break; }
                    if (reduce <= (3u << i)) { dictSize = 3u << i; break; }
                }
            }

            var enc = new SevenZip.Compression.LZMA.Encoder();
            enc.SetCoderProperties(
                new[]
                {
                    SevenZip.CoderPropID.DictionarySize,
                    SevenZip.CoderPropID.LitContextBits,
                    SevenZip.CoderPropID.LitPosBits,
                    SevenZip.CoderPropID.PosStateBits,
                    SevenZip.CoderPropID.EndMarker,
                },
                new object[] { (int)dictSize, 3, 0, 2, false });

            using (var inMs = new MemoryStream(plain, false))
            using (var outMs = new MemoryStream())
            {
                // Deliberately no WriteCoderProperties call — CHD omits the 5-byte props header.
                enc.Code(inMs, outMs, plain.Length, -1, null);
                return outMs.ToArray();
            }
        }

        private static uint FourCc(string s) =>
            (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);

        // MSB-first bit writer matching the reader's MSB-first consumption.
        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = new List<byte>();
            private uint _acc;
            private int _accBits;

            public void Write(uint value, int numBits)
            {
                for (int i = numBits - 1; i >= 0; i--)
                {
                    _acc = (_acc << 1) | ((value >> i) & 1);
                    _accBits++;
                    if (_accBits == 8)
                    {
                        _bytes.Add((byte)_acc);
                        _acc = 0;
                        _accBits = 0;
                    }
                }
            }

            public byte[] ToArray()
            {
                if (_accBits > 0)
                {
                    _acc <<= (8 - _accBits);
                    _bytes.Add((byte)_acc);
                    _acc = 0;
                    _accBits = 0;
                }
                return _bytes.ToArray();
            }
        }

        #endregion

        #region byte helpers

        private static void WriteU32LE(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static void WriteU64LE(byte[] b, int o, ulong v)
        {
            WriteU32LE(b, o, (uint)v);
            WriteU32LE(b, o + 4, (uint)(v >> 32));
        }

        private static void WriteU16BE(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v >> 8); b[o + 1] = (byte)v;
        }

        private static void WriteU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void WriteU48BE(byte[] b, int o, ulong v)
        {
            b[o] = (byte)(v >> 40); b[o + 1] = (byte)(v >> 32); b[o + 2] = (byte)(v >> 24);
            b[o + 3] = (byte)(v >> 16); b[o + 4] = (byte)(v >> 8); b[o + 5] = (byte)v;
        }

        private static void WriteU64BE(byte[] b, int o, ulong v)
        {
            WriteU32BE(b, o, (uint)(v >> 32));
            WriteU32BE(b, o + 4, (uint)v);
        }

        #endregion
    }
}
