using EmuLibrary.Util.ScanConcurrency;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace EmuLibrary.Tests.Util
{
    // Phase 1 governor tests. ScanEndpointKey.DriveToUnc is a static seam, so the mapped-drive tests
    // reset it in a try/finally to avoid leaking the fake resolver into other tests.
    public class ScanConcurrencyTests
    {
        // ---- Key derivation ----------------------------------------------------------------

        [Fact]
        public void UncPathsOnSameHost_DifferentShares_ShareKey()
        {
            var a = ScanEndpointKey.For(@"\\srv\a\x");
            var b = ScanEndpointKey.For(@"\\srv\b\y");
            Assert.Equal(a.Key, b.Key);
            Assert.True(a.IsNetwork);
            Assert.True(b.IsNetwork);
            Assert.Equal("unc:srv", a.Key);
        }

        [Fact]
        public void UncPathsOnDifferentHosts_HaveDifferentKeys()
        {
            var a = ScanEndpointKey.For(@"\\srvA\a\x");
            var b = ScanEndpointKey.For(@"\\srvB\a\x");
            Assert.NotEqual(a.Key, b.Key);
        }

        [Fact]
        public void UncHostParsing_IsCaseInsensitive()
        {
            var a = ScanEndpointKey.For(@"\\SRV\Share");
            var b = ScanEndpointKey.For(@"\\srv\other");
            Assert.Equal(a.Key, b.Key);
        }

        [Fact]
        public void BareUncHost_NoShare_ParsesToHostKey()
        {
            var a = ScanEndpointKey.For(@"\\srv");
            Assert.Equal("unc:srv", a.Key);
            Assert.True(a.IsNetwork);
        }

        [Fact]
        public void LocalDrivePath_IsLocalVolumeKey()
        {
            // The test box's system drive is fixed; derive expected from the actual root.
            var ep = ScanEndpointKey.For(@"C:\games\rom.nes");
            Assert.Equal("vol:c", ep.Key);
            Assert.False(ep.IsNetwork);
        }

        [Fact]
        public void EmptyOrNullPath_FallsBackToRemoteKey_NoThrow()
        {
            var empty = ScanEndpointKey.For("");
            var nul = ScanEndpointKey.For(null);
            Assert.Equal("", empty.Key);
            Assert.True(empty.IsNetwork);
            Assert.Equal("", nul.Key);
            Assert.True(nul.IsNetwork);
        }

        // ---- Mapped-drive coalescing (via the DriveToUnc seam) ------------------------------

        [Fact]
        public void NetworkMappedDrive_ResolvingToUnc_CoalescesWithUncHost()
        {
            WithSeams(DriveType.Network, drive => @"\\srv\share", () =>
            {
                var fromDrive = ScanEndpointKey.For(@"Z:\games\rom.nsp");
                var fromUnc = ScanEndpointKey.For(@"\\srv\a\x");
                Assert.Equal(fromUnc.Key, fromDrive.Key);
                Assert.True(fromDrive.IsNetwork);
            });
        }

        [Fact]
        public void NetworkMappedDrive_UnresolvableUnc_FallsBackToDriveKey_StillNetwork()
        {
            WithSeams(DriveType.Network, drive => null, () =>
            {
                var ep = ScanEndpointKey.For(@"Z:\games\rom.nsp");
                Assert.Equal("drive:z", ep.Key);
                Assert.True(ep.IsNetwork);
            });
        }

        private static void WithSeams(DriveType driveType, Func<string, string> driveToUnc, Action body)
        {
            var origType = ScanEndpointKey.DriveTypeOf;
            var origUnc = ScanEndpointKey.DriveToUnc;
            try
            {
                ScanEndpointKey.DriveTypeOf = _ => driveType;
                ScanEndpointKey.DriveToUnc = driveToUnc;
                body();
            }
            finally
            {
                ScanEndpointKey.DriveTypeOf = origType;
                ScanEndpointKey.DriveToUnc = origUnc;
            }
        }

        // ---- Governor: endpoint bound -------------------------------------------------------

        [Fact]
        public void Map_NeverExceeds_PerEndpointNetworkCap()
        {
            const int NetworkCap = ScanConcurrencyGovernor.NetworkPerEndpoint;
            var gov = new ScanConcurrencyGovernor(null);
            int current = 0, peak = 0;
            var items = Enumerable.Range(0, 200).ToList();

            gov.Map(items, @"\\srv\share", i =>
            {
                int now = Interlocked.Increment(ref current);
                UpdatePeak(ref peak, now);
                Thread.Sleep(2);
                Interlocked.Decrement(ref current);
                return i;
            }, CancellationToken.None);

            Assert.True(peak <= NetworkCap, $"peak {peak} exceeded network cap {NetworkCap}");
        }

        [Fact]
        public void Map_NeverExceeds_PerEndpointLocalCap()
        {
            const int LocalCap = ScanConcurrencyGovernor.LocalPerEndpoint;
            var gov = new ScanConcurrencyGovernor(null);
            int current = 0, peak = 0;
            var items = Enumerable.Range(0, 100).ToList();

            gov.Map(items, @"C:\games", i =>
            {
                int now = Interlocked.Increment(ref current);
                UpdatePeak(ref peak, now);
                Thread.Sleep(2);
                Interlocked.Decrement(ref current);
                return i;
            }, CancellationToken.None);

            Assert.True(peak <= LocalCap, $"peak {peak} exceeded local cap {LocalCap}");
        }

        [Fact]
        public void Map_ReturnsAllResults()
        {
            var gov = new ScanConcurrencyGovernor(null);
            var items = Enumerable.Range(0, 50).ToList();
            var results = gov.Map(items, @"\\srv\share", i => i * 2, CancellationToken.None);
            Assert.Equal(items.Select(i => i * 2).OrderBy(x => x),
                         results.OrderBy(x => x));
        }

        // ---- Governor: global bound ---------------------------------------------------------

        [Fact]
        public void Map_AcrossDistinctEndpoints_NeverExceedsGlobalMax()
        {
            const int GlobalMax = ScanConcurrencyGovernor.GlobalMax;
            var gov = new ScanConcurrencyGovernor(null);
            int current = 0, peak = 0;

            // Four distinct network hosts each at the per-endpoint network cap would far exceed the global
            // ceiling if unbounded; the global ceiling must hold them to GlobalMax. Run them concurrently
            // from separate threads.
            var hosts = new[] { @"\\h1\s", @"\\h2\s", @"\\h3\s", @"\\h4\s" };
            var items = Enumerable.Range(0, 60).ToList();

            Func<int, int> work = i =>
            {
                int now = Interlocked.Increment(ref current);
                UpdatePeak(ref peak, now);
                Thread.Sleep(2);
                Interlocked.Decrement(ref current);
                return i;
            };

            var threads = hosts.Select(h => new Thread(() =>
                gov.Map(items, h, work, CancellationToken.None))).ToList();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            Assert.True(peak <= GlobalMax, $"peak {peak} exceeded global max {GlobalMax}");
        }

        // ---- Cancellation -------------------------------------------------------------------

        [Fact]
        public void Map_PreCancelledToken_StopsPromptly_AndReleasesPermits()
        {
            var gov = new ScanConcurrencyGovernor(null);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            int ran = 0;
            var items = Enumerable.Range(0, 100).ToList();

            Assert.Throws<OperationCanceledException>(() =>
                gov.Map(items, @"\\srv\share", i => { Interlocked.Increment(ref ran); return i; }, cts.Token));

            Assert.Equal(0, ran);

            // A follow-up Run on the same endpoint must still acquire — proves no permit leaked.
            bool followupRan = false;
            gov.Run(@"\\srv\share", () => { followupRan = true; }, CancellationToken.None);
            Assert.True(followupRan);
        }

        [Fact]
        public void Run_PreCancelledToken_Throws_AndReleasesPermits()
        {
            var gov = new ScanConcurrencyGovernor(null);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                gov.Run(@"\\srv\share", () => 1, cts.Token));

            bool followupRan = false;
            gov.Run(@"\\srv\share", () => { followupRan = true; }, CancellationToken.None);
            Assert.True(followupRan);
        }

        [Fact]
        public void Run_ReturnsWorkResult()
        {
            var gov = new ScanConcurrencyGovernor(null);
            var result = gov.Run(@"C:\x", () => 42, CancellationToken.None);
            Assert.Equal(42, result);
        }

        private static void UpdatePeak(ref int peak, int candidate)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref peak);
                if (candidate <= observed) return;
            }
            while (Interlocked.CompareExchange(ref peak, candidate, observed) != observed);
        }
    }
}
