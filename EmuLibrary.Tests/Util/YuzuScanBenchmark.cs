using EmuLibrary.RomTypes.Yuzu;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace EmuLibrary.Tests.Util
{
    // Manual, opt-in benchmark for the libhac-driven Yuzu source scan. NOT a CI test: it no-ops unless
    // EL_YUZU_BENCH_DIR points at a folder of real Switch dumps (xci/xcz/nsp/nsz), and it needs prod.keys /
    // title.keys in %APPDATA%\yuzu\keys or ~/.switch.
    //
    // It isolates the per-file libhac parse cost (GetExternalGameFileInfo) from the directory walk, so the
    // same file set can be timed before/after a change to the parse path.
    //
    //   EL_YUZU_BENCH_DIR    source folder to scan (required to run; otherwise the test is a no-op)
    //   EL_YUZU_BENCH_LIMIT  cap on number of files parsed (optional; default = all)
    //   EL_YUZU_BENCH_SKIP   number of enumerated files to skip before parsing (optional; default = 0).
    //                        Use a non-zero skip to parse a set the OS/SMB cache hasn't warmed (cold run).
    //   EL_YUZU_BENCH_BASE   emulator base path for key loading (optional; keys also load from ~/.switch)
    //   EL_YUZU_BENCH_THREADS degree of parallelism for the parse (optional; default 1 = sequential). Each
    //                        worker gets its own Yuzu/Keyset, so this measures the parallel ceiling safely.
    //
    // Run a single benchmark with, e.g.:
    //   $env:EL_YUZU_BENCH_DIR='\\NAS\share\switch-dumps'; $env:EL_YUZU_BENCH_LIMIT='200'
    //   dotnet test --filter FullyQualifiedName~YuzuScanBenchmark
    public class YuzuScanBenchmark
    {
        private static readonly HashSet<string> ValidGameExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xci", ".xcz", ".nsp", ".nsz" };

        private readonly ITestOutputHelper _out;

        public YuzuScanBenchmark(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public void BenchmarkSourceScan()
        {
            var dir = Environment.GetEnvironmentVariable("EL_YUZU_BENCH_DIR");
            if (string.IsNullOrWhiteSpace(dir))
            {
                _out.WriteLine("EL_YUZU_BENCH_DIR not set; skipping benchmark.");
                return;
            }

            Assert.True(Directory.Exists(dir), $"EL_YUZU_BENCH_DIR \"{dir}\" does not exist.");

            int limit = int.MaxValue;
            var limitVar = Environment.GetEnvironmentVariable("EL_YUZU_BENCH_LIMIT");
            if (!string.IsNullOrWhiteSpace(limitVar) && int.TryParse(limitVar, out var parsed) && parsed > 0)
                limit = parsed;

            int skip = 0;
            var skipVar = Environment.GetEnvironmentVariable("EL_YUZU_BENCH_SKIP");
            if (!string.IsNullOrWhiteSpace(skipVar) && int.TryParse(skipVar, out var parsedSkip) && parsedSkip > 0)
                skip = parsedSkip;

            int threads = 1;
            var threadsVar = Environment.GetEnvironmentVariable("EL_YUZU_BENCH_THREADS");
            if (!string.IsNullOrWhiteSpace(threadsVar) && int.TryParse(threadsVar, out var parsedThreads) && parsedThreads > 0)
                threads = parsedThreads;

            var basePath = Environment.GetEnvironmentVariable("EL_YUZU_BENCH_BASE") ?? dir;
            var logger = LogManager.GetLogger();

            // 1. Directory walk (network enumeration), timed separately from parsing.
            var walk = Stopwatch.StartNew();
            var files = Directory
                .EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(f => ValidGameExtensions.Contains(Path.GetExtension(f)))
                .Skip(skip)
                .Take(limit)
                .ToList();
            walk.Stop();

            _out.WriteLine($"Dir: {dir}");
            _out.WriteLine($"Walk: {files.Count} candidate files in {walk.ElapsedMilliseconds} ms");
            _out.WriteLine($"Threads: {threads}");

            if (files.Count == 0)
                return;

            // 2. Per-file libhac parse, aggregate timed. Each worker gets its own Yuzu (own Keyset) so the
            //    parallel path never races on the shared ExternalKeySet that ImportTickets mutates.
            int ok = 0, failed = 0;
            long totalBytes = 0;
            var perFileMs = new System.Collections.Concurrent.ConcurrentBag<(string Path, long Ms)>();

            var parse = Stopwatch.StartNew();
            if (threads <= 1)
            {
                var yuzu = new Yuzu(basePath, logger, scanCache: null);
                foreach (var f in files)
                    ParseOne(yuzu, f, ref ok, ref failed, ref totalBytes, perFileMs);
            }
            else
            {
                var threadYuzu = new ThreadLocal<Yuzu>(
                    () => new Yuzu(basePath, logger, scanCache: null));
                int okL = 0, failedL = 0;
                long bytesL = 0;
                System.Threading.Tasks.Parallel.ForEach(
                    files,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
                    f =>
                    {
                        var sw = Stopwatch.StartNew();
                        bool good = false;
                        try { good = threadYuzu.Value.GetExternalGameFileInfo(f) != null; }
                        catch { good = false; }
                        sw.Stop();
                        perFileMs.Add((f, sw.ElapsedMilliseconds));
                        if (good) Interlocked.Increment(ref okL); else Interlocked.Increment(ref failedL);
                        try { Interlocked.Add(ref bytesL, new FileInfo(f).Length); } catch { /* ignore */ }
                    });
                ok = okL; failed = failedL; totalBytes = bytesL;
            }
            parse.Stop();

            double seconds = parse.Elapsed.TotalSeconds;
            double mb = totalBytes / (1024.0 * 1024.0);

            _out.WriteLine($"Parse: {files.Count} files in {parse.ElapsedMilliseconds} ms " +
                           $"({(files.Count / Math.Max(seconds, 0.001)):F1} files/s)");
            _out.WriteLine($"  parsed ok: {ok}, failed/null: {failed}");
            _out.WriteLine($"  avg: {(parse.ElapsedMilliseconds / (double)files.Count):F1} ms/file wall, " +
                           $"data touched: {mb:F0} MB");

            _out.WriteLine("Slowest 10:");
            foreach (var s in perFileMs.OrderByDescending(s => s.Ms).Take(10))
                _out.WriteLine($"  {s.Ms,6} ms  {Path.GetFileName(s.Path)}");
        }

        private static void ParseOne(Yuzu yuzu, string f, ref int ok, ref int failed, ref long totalBytes,
            System.Collections.Concurrent.ConcurrentBag<(string, long)> perFileMs)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var info = yuzu.GetExternalGameFileInfo(f);
                if (info != null) ok++; else failed++;
            }
            catch
            {
                failed++;
            }
            sw.Stop();
            perFileMs.Add((f, sw.ElapsedMilliseconds));
            try { totalBytes += new FileInfo(f).Length; } catch { /* ignore */ }
        }
    }
}
