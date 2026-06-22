using EmuLibrary.Util.ZArchive;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace EmuLibrary.Tests.Util
{
    // Validates the ZArchive (.wua) reader as the exact inverse of the writer: it must recover the directory
    // tree, list children, and return byte-identical file contents across the compressible (zstd) branch, the
    // incompressible (stored-raw) branch, multi-block files, and a file whose data starts mid-block. This is
    // what lets the scanner read pre-made .wua sources.
    public class ZArchiveReaderTests
    {
        [Fact]
        public void RoundTrips_Tree_Listing_AndFileContents()
        {
            // A small file (single sub-block), a highly compressible large file (multiple zstd blocks, ends
            // mid-block), and an incompressible large file (stored-raw blocks). The small file is written first
            // so the big files' data starts mid-block, exercising the within-block offset path.
            var meta = Encoding.UTF8.GetBytes("<menu><longname_en>Test</longname_en></menu>");
            var compressible = Pattern(200_000);
            var incompressible = Random(150_000, seed: 99);

            var files = new Dictionary<string, byte[]>
            {
                ["0005000010102000_v16/meta/meta.xml"] = meta,
                ["0005000010102000_v16/code/app.rpx"] = compressible,
                ["0005000010102000_v16/content/data.bin"] = incompressible,
            };

            byte[] archive;
            using (var ms = new MemoryStream())
            {
                using (var w = new ZArchiveWriter(ms))
                {
                    foreach (var kv in files)
                    {
                        int slash = kv.Key.LastIndexOf('/');
                        Assert.True(w.MakeDir(kv.Key.Substring(0, slash), true));
                        Assert.True(w.StartNewFile(kv.Key));
                        w.AppendData(kv.Value, 0, kv.Value.Length);
                    }
                    w.Complete();
                }
                archive = ms.ToArray();
            }

            using (var r = ZArchiveReader.Open(new MemoryStream(archive)))
            {
                // Top-level folder is the title unit; subdirectories are the loadiine tree.
                var roots = r.ListSubdirectories("");
                Assert.Equal(new[] { "0005000010102000_v16" }, roots.ToArray());

                Assert.True(r.DirectoryExists("0005000010102000_v16"));
                Assert.True(r.DirectoryExists("0005000010102000_v16/meta"));
                Assert.False(r.DirectoryExists("0005000010102000_v16/meta/meta.xml"));

                var sub = r.ListSubdirectories("0005000010102000_v16").OrderBy(x => x).ToArray();
                Assert.Equal(new[] { "code", "content", "meta" }, sub);

                Assert.True(r.FileExists("0005000010102000_v16/meta/meta.xml"));
                Assert.False(r.FileExists("0005000010102000_v16/missing.bin"));

                // Case-insensitive lookup (ZArchive paths are case-insensitive).
                Assert.True(r.FileExists("0005000010102000_V16/META/Meta.XML"));

                // Byte-identical content across all three branches.
                foreach (var kv in files)
                {
                    Assert.True(r.TryReadFile(kv.Key, out var got));
                    Assert.Equal(kv.Value, got);
                }

                Assert.False(r.TryReadFile("0005000010102000_v16/nope", out _));
            }
        }

        [Fact]
        public void Rejects_NonArchive()
        {
            var junk = new byte[200];
            Assert.ThrowsAny<Exception>(() => ZArchiveReader.Open(new MemoryStream(junk)));
        }

        private static byte[] Pattern(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++)
                b[i] = (byte)(i % 7);
            return b;
        }

        private static byte[] Random(int n, ulong seed)
        {
            var b = new byte[n];
            ulong s = seed;
            for (int i = 0; i < n; i++)
            {
                s = s * 6364136223846793005UL + 1442695040888963407UL;
                b[i] = (byte)(s >> 56);
            }
            return b;
        }
    }
}
