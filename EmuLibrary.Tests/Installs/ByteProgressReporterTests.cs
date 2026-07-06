using System;
using System.Collections.Generic;
using System.Threading;
using EmuLibrary.Installs;
using Playnite.SDK.Models;
using Xunit;

namespace EmuLibrary.Tests.Installs
{
    // Phase 2 of the Installs panel: the crypto/extract primitives (3DS decrypt, PS3 PKG extract) report
    // INCREMENTAL bytes per chunk; ByteProgressReporter accumulates them into the handle's cumulative
    // SetBytes(done, total). These pin that accumulation.
    public class ByteProgressReporterTests
    {
        // Records every SetBytes call so we can assert the cumulative sequence without the handle's throttle.
        private sealed class RecordingHandle : IInstallProgressHandle
        {
            public readonly List<(long done, long total)> Bytes = new List<(long, long)>();
            public void SetBytes(long done, long total) => Bytes.Add((done, total));
            public void SetStage(string text) { }
            public void SetIndeterminate(string text) { }
            public void SetPercent(double pct) { }
            public void Complete() { }
            public void Fail(Exception ex) { }
            public void Canceled() { }
        }

        [Fact]
        public void Report_AccumulatesIncrements_ForwardsCumulativeWithTotal()
        {
            const long total = 4L * 1024 * 1024;
            var handle = new RecordingHandle();
            IProgress<long> reporter = new ByteProgressReporter(handle, total);

            reporter.Report(1024 * 1024);
            reporter.Report(2 * 1024 * 1024);
            reporter.Report(1024 * 1024);

            Assert.Equal(new[]
            {
                (1L * 1024 * 1024, total),
                (3L * 1024 * 1024, total),
                (4L * 1024 * 1024, total),
            }, handle.Bytes);
        }

        [Fact]
        public void Report_IgnoresNonPositiveIncrements()
        {
            var handle = new RecordingHandle();
            IProgress<long> reporter = new ByteProgressReporter(handle, 100);

            reporter.Report(0);
            reporter.Report(-50);
            reporter.Report(10);

            var only = Assert.Single(handle.Bytes);
            Assert.Equal((10L, 100L), only);
        }

        [Fact]
        public void Report_DrivesTheLiveItem_EndsAtTotal()
        {
            // End-to-end through the real service (synchronous dispatcher): a cumulative sequence that reaches
            // the total force-flushes the throttle, so the bound item lands exactly at the total, determinate.
            var svc = new InstallProgressService(new SyncDispatcher(), _ => null, null);
            var handle = svc.Begin(new Game("3DS Title") { Id = Guid.NewGuid() }, new CancellationTokenSource());
            var item = svc.ViewModel.Active[0];

            const long total = 8L * 1024 * 1024;
            IProgress<long> reporter = new ByteProgressReporter(handle, total);
            for (int i = 0; i < 8; i++)
                reporter.Report(1024 * 1024);

            Assert.False(item.IsIndeterminate);
            Assert.Equal(total, item.ProgressMaximum);
            Assert.Equal(total, item.ProgressValue);
        }

        private sealed class SyncDispatcher : IUiDispatcher
        {
            public void Invoke(Action action) => action();
        }
    }
}
