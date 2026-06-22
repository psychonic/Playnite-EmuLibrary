using EmuLibrary.Util.ZArchive;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;
using ZstdSharp;

namespace EmuLibrary.Tests.Util
{
    // Validates the pure-C# ZArchive (.wua) writer against the ZArchive format contract: the footer
    // (magic/version/total size), the whole-archive SHA-256, and a full round-trip of the compressed data
    // section reconstructed from the offset records (covering both the zstd-compressed and stored-raw block
    // branches and the final zero-padding). This is the byte-exactness guard the plan flagged as the main
    // risk for the writer.
    public class ZArchiveWriterTests
    {
        private const int Block = 64 * 1024;
        private const uint Magic = 0x169f52d6;
        private const uint Version = 0x61bf3a01;
        private const int FooterSize = 144;

        [Fact]
        public void Compressible_RoundTripsAndIntegrityHolds()
        {
            // Not block-aligned -> exercises the final partial-block zero-padding; highly compressible -> the
            // zstd-compressed block branch.
            var data = new byte[100_000];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i % 7);

            AssertArchiveRoundTrips(data);
        }

        [Fact]
        public void Incompressible_StoresRawAndRoundTrips()
        {
            // Pseudo-random, incompressible data -> blocks are stored raw (compressed >= block size branch).
            var data = new byte[140_000];
            ulong lcg = 0x1234_5678_9abc_def0UL;
            for (int i = 0; i < data.Length; i++)
            {
                lcg = lcg * 6364136223846793005UL + 1442695040888963407UL;
                data[i] = (byte)(lcg >> 56);
            }

            AssertArchiveRoundTrips(data);
        }

        private static void AssertArchiveRoundTrips(byte[] fileData)
        {
            byte[] archive;
            using (var ms = new MemoryStream())
            {
                using (var w = new ZArchiveWriter(ms))
                {
                    Assert.True(w.MakeDir("dir", true));
                    Assert.True(w.StartNewFile("dir/payload.bin"));
                    w.AppendData(fileData, 0, fileData.Length);
                    w.Complete();
                }
                archive = ms.ToArray();
            }

            int len = archive.Length;
            int footer = len - FooterSize;
            Assert.True(footer > 0);

            // Footer trailer fields.
            Assert.Equal(Version, U32(archive, footer + 136));
            Assert.Equal(Magic, U32(archive, footer + 140));
            Assert.Equal((ulong)len, U64(archive, footer + 128)); // totalSize == file length

            // Whole-archive SHA-256: hash everything up to the footer, plus the footer with its 32-byte hash
            // field zeroed, must equal the stored hash.
            var storedHash = new byte[32];
            Buffer.BlockCopy(archive, footer + 96, storedHash, 0, 32);
            using (var sha = SHA256.Create())
            {
                sha.TransformBlock(archive, 0, footer, null, 0);
                var zeroedFooter = new byte[FooterSize];
                Buffer.BlockCopy(archive, footer, zeroedFooter, 0, FooterSize);
                Array.Clear(zeroedFooter, 96, 32);
                sha.TransformFinalBlock(zeroedFooter, 0, FooterSize);
                Assert.Equal(sha.Hash, storedHash);
            }

            // Section offsets: compressed data starts at 0; offset records section follows.
            ulong compOff = U64(archive, footer + 0), compSize = U64(archive, footer + 8);
            ulong recOff = U64(archive, footer + 16), recSize = U64(archive, footer + 24);
            Assert.Equal(0UL, compOff);

            // Reconstruct the concatenated input from the offset records and assert the file's bytes survive.
            var reconstructed = Reconstruct(archive, compSize, recOff, recSize);
            Assert.True(reconstructed.Length >= fileData.Length);
            for (int i = 0; i < fileData.Length; i++)
                Assert.Equal(fileData[i], reconstructed[i]);

            // Input is zero-padded to a whole number of 64 KiB blocks.
            Assert.Equal(0, reconstructed.Length % Block);
        }

        // Walks the compressed-data section block-by-block using the offset records and returns the
        // concatenated decompressed input. Also asserts each record's stored base offset matches the running
        // compressed offset.
        private static byte[] Reconstruct(byte[] archive, ulong compSize, ulong recOff, ulong recSize)
        {
            using (var dec = new Decompressor())
            using (var outMs = new MemoryStream())
            {
                int blockIndex = 0;
                ulong consumed = 0;
                while (consumed < compSize)
                {
                    int record = blockIndex / 16;
                    int slot = blockIndex % 16;
                    long recBase = (long)recOff + record * 40;

                    ulong baseOffset = U64(archive, (int)recBase);
                    ulong blockOffset = baseOffset;
                    for (int k = 0; k < slot; k++)
                        blockOffset += (uint)U16(archive, (int)recBase + 8 + 2 * k) + 1u;

                    // The first slot's base offset must equal the running compressed offset.
                    if (slot == 0)
                        Assert.Equal(consumed, baseOffset);

                    int compLen = U16(archive, (int)recBase + 8 + 2 * slot) + 1;

                    var block = new byte[Block];
                    if (compLen == Block)
                    {
                        Buffer.BlockCopy(archive, (int)blockOffset, block, 0, Block);
                    }
                    else
                    {
                        int n = dec.Unwrap(archive, (int)blockOffset, compLen, block, 0, Block);
                        Assert.Equal(Block, n);
                    }
                    outMs.Write(block, 0, Block);

                    consumed += (ulong)compLen;
                    blockIndex++;
                }
                Assert.Equal(compSize, consumed);
                return outMs.ToArray();
            }
        }

        private static ushort U16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        private static uint U32(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static ulong U64(byte[] b, int o) => ((ulong)U32(b, o) << 32) | U32(b, o + 4);
    }
}
