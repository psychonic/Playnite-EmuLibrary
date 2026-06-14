using EmuLibrary.RomTypes.Yuzu;
using EmuLibrary.Util.ScanCache;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace EmuLibrary.Tests.Util
{
    public class ScanCacheTests : IDisposable
    {
        private readonly string _path;

        public ScanCacheTests()
        {
            _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".json");
        }

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
            var tmp = _path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        private sealed class SimpleValue
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        private sealed class OtherValue
        {
            public string Data { get; set; }
        }

        // 1. FileStamp value equality
        [Fact]
        public void FileStamp_EqualStamps_AreEqual()
        {
            var a = new FileStamp(1234L, 9876543210L);
            var b = new FileStamp(1234L, 9876543210L);
            Assert.True(a.Equals(b));
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void FileStamp_DifferentSize_NotEqual()
        {
            var a = new FileStamp(1234L, 9876543210L);
            var b = new FileStamp(5678L, 9876543210L);
            Assert.False(a.Equals(b));
        }

        [Fact]
        public void FileStamp_DifferentMtime_NotEqual()
        {
            var a = new FileStamp(1234L, 9876543210L);
            var b = new FileStamp(1234L, 1111111111L);
            Assert.False(a.Equals(b));
        }

        // 2. Round-trip
        [Fact]
        public void RoundTrip_SetThenGet_ReturnsValue()
        {
            var cache = new JsonScanCache(_path, null);
            var stamp = new FileStamp(100L, 200L);
            var value = new SimpleValue { Name = "test", Count = 42 };

            cache.Set("C:\\foo\\bar.nsp", stamp, value);
            var hit = cache.TryGet<SimpleValue>("C:\\foo\\bar.nsp", stamp, out var result);

            Assert.True(hit);
            Assert.NotNull(result);
            Assert.Equal("test", result.Name);
            Assert.Equal(42, result.Count);
        }

        // 3. Stamp mismatch → miss
        [Fact]
        public void StampMismatch_WrongSize_Miss()
        {
            var cache = new JsonScanCache(_path, null);
            var setStamp = new FileStamp(100L, 200L);
            var getStamp = new FileStamp(999L, 200L);
            cache.Set("C:\\foo\\bar.nsp", setStamp, new SimpleValue { Name = "x" });

            var hit = cache.TryGet<SimpleValue>("C:\\foo\\bar.nsp", getStamp, out var result);

            Assert.False(hit);
            Assert.Null(result);
        }

        [Fact]
        public void StampMismatch_WrongMtime_Miss()
        {
            var cache = new JsonScanCache(_path, null);
            var setStamp = new FileStamp(100L, 200L);
            var getStamp = new FileStamp(100L, 999L);
            cache.Set("C:\\foo\\bar.nsp", setStamp, new SimpleValue { Name = "x" });

            var hit = cache.TryGet<SimpleValue>("C:\\foo\\bar.nsp", getStamp, out var result);

            Assert.False(hit);
            Assert.Null(result);
        }

        // 4. Type mismatch → miss
        [Fact]
        public void TypeMismatch_DifferentType_Miss()
        {
            var cache = new JsonScanCache(_path, null);
            var stamp = new FileStamp(100L, 200L);
            cache.Set<SimpleValue>("C:\\foo\\bar.nsp", stamp, new SimpleValue { Name = "x" });

            var hit = cache.TryGet<OtherValue>("C:\\foo\\bar.nsp", stamp, out var result);

            Assert.False(hit);
            Assert.Null(result);
        }

        // 5. Persistence across instances
        [Fact]
        public void Persistence_FlushAndReload_Hits()
        {
            var stamp = new FileStamp(512L, 637000000000L);
            var value = new SimpleValue { Name = "persistent", Count = 7 };

            var cache1 = new JsonScanCache(_path, null);
            cache1.Set("C:\\roms\\game.nsp", stamp, value);
            cache1.Flush();

            var cache2 = new JsonScanCache(_path, null);
            var hit = cache2.TryGet<SimpleValue>("C:\\roms\\game.nsp", stamp, out var result);

            Assert.True(hit);
            Assert.Equal("persistent", result.Name);
            Assert.Equal(7, result.Count);
        }

        // 6. Version invalidation
        [Fact]
        public void FormatVersionMismatch_TreatedAsEmpty()
        {
            // Write a store with wrong version
            File.WriteAllText(_path, "{\"Version\":99,\"Entries\":{\"C:\\\\foo.nsp\":{\"Size\":1,\"MTicks\":2,\"TypeName\":\"Whatever\",\"Json\":\"{}\"}}}");

            var cache = new JsonScanCache(_path, null);
            var hit = cache.TryGet<SimpleValue>("C:\\foo.nsp", new FileStamp(1L, 2L), out var result);

            Assert.False(hit);
            Assert.Null(result);
        }

        // 7. Corrupt file — ctor does not throw, behaves empty
        [Fact]
        public void CorruptFile_DoesNotThrow_BehavesEmpty()
        {
            File.WriteAllText(_path, "not valid json at all!!!");

            var cache = new JsonScanCache(_path, null);
            var hit = cache.TryGet<SimpleValue>("C:\\foo.nsp", new FileStamp(1L, 2L), out var result);

            Assert.False(hit);
            Assert.Null(result);
        }

        // 8. RemoveKeysUnder — prunes entries not in visited set within the directory
        [Fact]
        public void RemoveKeysUnder_StaleEntry_IsRemoved()
        {
            var cache = new JsonScanCache(_path, null);
            var stamp = new FileStamp(100L, 200L);
            cache.Set("C:\\roms\\deleted.nsp", stamp, new SimpleValue { Name = "stale" });
            cache.Set("C:\\roms\\kept.nsp", stamp, new SimpleValue { Name = "alive" });

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C:\\roms\\kept.nsp" };
            cache.RemoveKeysUnder("C:\\roms", visited);

            Assert.False(cache.TryGet<SimpleValue>("C:\\roms\\deleted.nsp", stamp, out _));
            Assert.True(cache.TryGet<SimpleValue>("C:\\roms\\kept.nsp", stamp, out _));
        }

        [Fact]
        public void RemoveKeysUnder_DoesNotTouchOtherDirectories()
        {
            var cache = new JsonScanCache(_path, null);
            var stamp = new FileStamp(100L, 200L);
            cache.Set("C:\\roms\\game.nsp", stamp, new SimpleValue { Name = "roms" });
            cache.Set("D:\\nand\\file.nca", stamp, new SimpleValue { Name = "nand" });

            // Scan C:\roms with no visited files → prunes roms entry but not nand entry
            cache.RemoveKeysUnder("C:\\roms", new HashSet<string>());

            Assert.False(cache.TryGet<SimpleValue>("C:\\roms\\game.nsp", stamp, out _));
            Assert.True(cache.TryGet<SimpleValue>("D:\\nand\\file.nca", stamp, out _));
        }

        [Fact]
        public void RemoveKeysUnder_PersistsAfterFlushAndReload()
        {
            var stamp = new FileStamp(100L, 200L);
            var cache1 = new JsonScanCache(_path, null);
            cache1.Set("C:\\roms\\old.nsp", stamp, new SimpleValue { Name = "old" });
            cache1.Set("C:\\roms\\new.nsp", stamp, new SimpleValue { Name = "new" });
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C:\\roms\\new.nsp" };
            cache1.RemoveKeysUnder("C:\\roms", visited);
            cache1.Flush();

            var cache2 = new JsonScanCache(_path, null);
            Assert.False(cache2.TryGet<SimpleValue>("C:\\roms\\old.nsp", stamp, out _));
            Assert.True(cache2.TryGet<SimpleValue>("C:\\roms\\new.nsp", stamp, out _));
        }

        // Bonus: round-trip ExternalGameFileInfo (the real type used by Yuzu scanner)
        [Fact]
        public void RoundTrip_ExternalGameFileInfo_Succeeds()
        {
            var cache = new JsonScanCache(_path, null);
            var stamp = new FileStamp(2048L, 637123456789L);
            var value = new Yuzu.ExternalGameFileInfo
            {
                FilePath = "C:\\switch\\game.nsp",
                FileType = Yuzu.FileType.NSP,
                TitleId = 0x0100ABC001234567UL,
                BaseTitleId = 0x0100ABC001234000UL,
                Type = Yuzu.ExternalGameFileType.Game,
                Version = 0,
                DisplayVersion = "1.0.0",
                TitleName = "My Switch Game",
                Publisher = "Some Publisher",
                LaunchSubPath = "000000AB\\abcdef0123456789.nca",
            };

            cache.Set("C:\\switch\\game.nsp", stamp, value);
            var hit = cache.TryGet<Yuzu.ExternalGameFileInfo>("C:\\switch\\game.nsp", stamp, out var result);

            Assert.True(hit);
            Assert.Equal(value.TitleId, result.TitleId);
            Assert.Equal(value.TitleName, result.TitleName);
            Assert.Equal(value.Publisher, result.Publisher);
            Assert.Equal(value.FilePath, result.FilePath);
            Assert.Equal(value.LaunchSubPath, result.LaunchSubPath);
            Assert.Equal(value.DisplayVersion, result.DisplayVersion);
        }
    }
}
