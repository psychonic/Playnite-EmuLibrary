using System;
using EmuLibrary.Installs;
using Xunit;

namespace EmuLibrary.Tests.Installs
{
    // Phase 3: the Installs panel shows throughput/ETA. ThroughputEstimator turns cumulative bytes-done samples
    // into a smoothed bytes/sec rate. These pin the seeding, the steady-state rate, and the phase-reset behavior.
    public class ThroughputEstimatorTests
    {
        // A hand-cranked clock so rate math is deterministic.
        private sealed class FakeClock
        {
            public DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
        }

        [Fact]
        public void FirstSample_HasNoRateYet()
        {
            var clock = new FakeClock();
            var est = new ThroughputEstimator(() => clock.Now);

            Assert.Null(est.Sample(0));
        }

        [Fact]
        public void SteadyRate_ConvergesToBytesPerSecond()
        {
            var clock = new FakeClock();
            var est = new ThroughputEstimator(() => clock.Now);

            // 1 MiB per second, sampled every second. The EMA converges toward 1 MiB/s.
            est.Sample(0);
            double? rate = null;
            for (int i = 1; i <= 20; i++)
            {
                clock.Advance(1.0);
                rate = est.Sample(i * 1024L * 1024L);
            }

            Assert.NotNull(rate);
            Assert.InRange(rate.Value, 1024 * 1024 * 0.98, 1024 * 1024 * 1.02);
        }

        [Fact]
        public void ZeroTimeTick_DoesNotThrowOrSpike()
        {
            var clock = new FakeClock();
            var est = new ThroughputEstimator(() => clock.Now);

            est.Sample(0);
            // Same timestamp: no elapsed time — must not divide-by-zero or invent a rate.
            Assert.Null(est.Sample(1024 * 1024));
        }

        [Fact]
        public void Reset_ReSeedsBaseline_ForANewPhase()
        {
            var clock = new FakeClock();
            var est = new ThroughputEstimator(() => clock.Now);

            est.Sample(0);
            clock.Advance(1.0);
            est.Sample(1024 * 1024);

            est.Reset();

            // After reset the first sample of the new phase re-seeds and reports nothing yet.
            Assert.Null(est.Sample(0));
        }

        [Fact]
        public void NonMonotonicSample_ReSeedsWithoutNegativeRate()
        {
            var clock = new FakeClock();
            var est = new ThroughputEstimator(() => clock.Now);

            est.Sample(0);
            clock.Advance(1.0);
            var established = est.Sample(2 * 1024 * 1024);
            Assert.NotNull(established);

            // done rewinds (a new phase reusing the handle): keep the prior rate, don't emit a negative one.
            clock.Advance(1.0);
            var afterRewind = est.Sample(0);
            Assert.True(afterRewind.Value > 0);
        }
    }
}
