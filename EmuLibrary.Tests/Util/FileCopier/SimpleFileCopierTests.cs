using EmuLibrary.Util.FileCopier;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace EmuLibrary.Tests.Util.FileCopier
{
    // Phase 1 of the Installs panel: SimpleFileCopier switched from File.Copy to a streamed loop that reports
    // byte progress and cleans up its partial output on cancellation. These pin that behaviour.
    public class SimpleFileCopierTests : IDisposable
    {
        private readonly string _root;

        public SimpleFileCopierTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "eltest_copier_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Src(string name) => Path.Combine(_root, name);

        private sealed class RecordingProgress : IProgress<CopyProgress>
        {
            public readonly List<CopyProgress> Reports = new List<CopyProgress>();
            public void Report(CopyProgress value) => Reports.Add(value);
        }

        [Fact]
        public async Task CopyFile_ReportsMonotonicProgressEndingAtTotal()
        {
            // A few MiB so the 1 MiB streaming loop produces several intermediate reports.
            var srcFile = Src("rom.bin");
            var bytes = new byte[5 * 1024 * 1024 + 123];
            new Random(1).NextBytes(bytes);
            File.WriteAllBytes(srcFile, bytes);

            var destDir = Path.Combine(_root, "dest");
            Directory.CreateDirectory(destDir);

            var progress = new RecordingProgress();
            await new SimpleFileCopier(new FileInfo(srcFile), new DirectoryInfo(destDir))
                .CopyAsync(CancellationToken.None, progress);

            var copied = Path.Combine(destDir, "rom.bin");
            Assert.True(File.Exists(copied));
            Assert.Equal(bytes, File.ReadAllBytes(copied));

            Assert.NotEmpty(progress.Reports);
            // Every report carries the true total.
            Assert.All(progress.Reports, r => Assert.Equal(bytes.Length, r.BytesTotal));
            // BytesDone never goes backwards.
            var done = progress.Reports.Select(r => r.BytesDone).ToList();
            for (int i = 1; i < done.Count; i++)
                Assert.True(done[i] >= done[i - 1], $"progress went backwards at {i}: {done[i - 1]} -> {done[i]}");
            // And ends exactly at the total.
            Assert.Equal(bytes.Length, done.Last());
        }

        [Fact]
        public async Task CopyDirectory_ReportsTotalAcrossAllFilesAndEndsAtTotal()
        {
            var srcDir = Path.Combine(_root, "game");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
            File.WriteAllBytes(Path.Combine(srcDir, "a.bin"), new byte[1500000]);
            File.WriteAllBytes(Path.Combine(srcDir, "sub", "b.bin"), new byte[1200000]);
            long expectedTotal = 1500000 + 1200000;

            var destDir = Path.Combine(_root, "dest", "game");

            var progress = new RecordingProgress();
            await new SimpleFileCopier(new DirectoryInfo(srcDir), new DirectoryInfo(destDir))
                .CopyAsync(CancellationToken.None, progress);

            Assert.True(File.Exists(Path.Combine(destDir, "a.bin")));
            Assert.True(File.Exists(Path.Combine(destDir, "sub", "b.bin")));

            Assert.All(progress.Reports, r => Assert.Equal(expectedTotal, r.BytesTotal));
            Assert.Equal(expectedTotal, progress.Reports.Last().BytesDone);
        }

        [Fact]
        public async Task Cancel_MidFile_DeletesPartialDestinationFile()
        {
            var srcFile = Src("big.bin");
            File.WriteAllBytes(srcFile, new byte[8 * 1024 * 1024]);

            var destDir = Path.Combine(_root, "dest");
            Directory.CreateDirectory(destDir);
            var copied = Path.Combine(destDir, "big.bin");

            var cts = new CancellationTokenSource();
            // Cancel once a chunk has actually been written (not on the pre-loop (0,total) report), so we abort
            // partway through the file and leave a real partial to clean up.
            var progress = new CallbackProgress(p => { if (p.BytesDone > 0) cts.Cancel(); });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new SimpleFileCopier(new FileInfo(srcFile), new DirectoryInfo(destDir)).CopyAsync(cts.Token, progress));

            Assert.False(File.Exists(copied), "partial destination file should have been cleaned up on cancel");
        }

        [Fact]
        public async Task Cancel_MidDirectory_DeletesPartialDestinationFolder()
        {
            var srcDir = Path.Combine(_root, "game");
            Directory.CreateDirectory(srcDir);
            for (int i = 0; i < 4; i++)
                File.WriteAllBytes(Path.Combine(srcDir, $"f{i}.bin"), new byte[2 * 1024 * 1024]);

            var destDir = Path.Combine(_root, "dest", "game");

            var cts = new CancellationTokenSource();
            var progress = new CallbackProgress(p => { if (p.BytesDone > 0) cts.Cancel(); });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new SimpleFileCopier(new DirectoryInfo(srcDir), new DirectoryInfo(destDir)).CopyAsync(cts.Token, progress));

            Assert.False(Directory.Exists(destDir), "partial destination folder should have been cleaned up on cancel");
        }

        private sealed class CallbackProgress : IProgress<CopyProgress>
        {
            private readonly Action<CopyProgress> _cb;
            public CallbackProgress(Action<CopyProgress> cb) => _cb = cb;
            public void Report(CopyProgress value) => _cb(value);
        }
    }
}
