using EmuLibrary.Util.Metadata;
using System;
using System.IO;
using System.Threading;
using Xunit;

namespace EmuLibrary.Tests.Util.Metadata
{
    public class IconCacheTests : IDisposable
    {
        private readonly string _root;

        public IconCacheTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "el-iconcache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
        }

        [Fact]
        public void EnsureIcon_WritesAndReturnsPath_UnderMetadataSubDir()
        {
            var bytes = new byte[] { 1, 2, 3, 4 };
            var path = IconCache.EnsureIcon(_root, "sub-icons", "KEY01", "png", () => bytes, null);

            Assert.NotNull(path);
            Assert.Equal(Path.Combine(_root, "metadata", "sub-icons", "KEY01.png"), path);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        [Fact]
        public void EnsureIcon_ExtractsOnce_ReusesFileOnSecondCall()
        {
            var calls = 0;
            Func<byte[]> extract = () => { calls++; return new byte[] { 9 }; };

            var first = IconCache.EnsureIcon(_root, "sub", "K", "png", extract, null);
            var second = IconCache.EnsureIcon(_root, "sub", "K", "png", extract, null);

            Assert.Equal(first, second);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void EnsureIcon_NullExtract_ReturnsNull_WritesNothing()
        {
            var path = IconCache.EnsureIcon(_root, "sub", "K", "png", () => null, null);

            Assert.Null(path);
            Assert.False(Directory.Exists(Path.Combine(_root, "metadata", "sub")));
        }

        [Fact]
        public void EnsureIcon_EmptyExtract_ReturnsNull()
        {
            var path = IconCache.EnsureIcon(_root, "sub", "K", "png", () => new byte[0], null);
            Assert.Null(path);
        }

        [Fact]
        public void EnsureIcon_ExtractThrows_ReturnsNull_FailSoft()
        {
            var path = IconCache.EnsureIcon(_root, "sub", "K", "png",
                () => throw new IOException("boom"), null);
            Assert.Null(path);
        }

        [Fact]
        public void EnsureIcon_PreExistingFile_ReusedWithoutExtracting()
        {
            var dir = Path.Combine(_root, "metadata", "sub");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "K.png");
            File.WriteAllBytes(dest, new byte[] { 7 });

            var called = false;
            var path = IconCache.EnsureIcon(_root, "sub", "K", "png",
                () => { called = true; return new byte[] { 8 }; }, null);

            Assert.Equal(dest, path);
            Assert.False(called);
            Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(dest));
        }

        [Fact]
        public void EnsureIcon_ConcurrentWriters_AllSucceed_SingleFile()
        {
            var threads = new Thread[8];
            var results = new string[threads.Length];
            var start = new ManualResetEventSlim(false);

            for (int i = 0; i < threads.Length; i++)
            {
                var idx = i;
                threads[i] = new Thread(() =>
                {
                    start.Wait();
                    results[idx] = IconCache.EnsureIcon(_root, "race", "K", "png",
                        () => new byte[] { 42 }, null);
                });
                threads[i].Start();
            }

            start.Set();
            foreach (var t in threads)
                t.Join();

            var expected = Path.Combine(_root, "metadata", "race", "K.png");
            foreach (var r in results)
                Assert.Equal(expected, r);
            Assert.True(File.Exists(expected));
            // No leftover temp files from losing writers.
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "metadata", "race"), "*.tmp"));
        }
    }
}
