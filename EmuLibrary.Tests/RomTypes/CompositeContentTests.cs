using System;
using System.Collections.Generic;
using System.Linq;
using EmuLibrary.RomTypes;
using Xunit;

namespace EmuLibrary.Tests.RomTypes
{
    public class CompositeContentTests
    {
        // A minimal update item: a version (the ordering key) and a tag to assert identity/order.
        private sealed class Upd
        {
            public Version Ver;
            public string Tag;
        }

        private static List<Upd> Sample()
        {
            // Deliberately out of order, with a numeric-vs-lexical trap: 01.10 must sort above 01.02.
            return new List<Upd>
            {
                new Upd { Ver = Version.Parse("1.2"),  Tag = "v0102" },
                new Upd { Ver = Version.Parse("1.10"), Tag = "v0110" },
                new Upd { Ver = Version.Parse("1.1"),  Tag = "v0101" },
            };
        }

        [Fact]
        public void AllInOrder_ReturnsEveryUpdate_AscendingByVersion_NotLexical()
        {
            var result = CompositeContent.SelectUpdatesToInstall(
                Sample(), UpdateInstallStrategy.InstallAllUpdatesInOrder, u => u.Ver);

            Assert.Equal(new[] { "v0101", "v0102", "v0110" }, result.Select(u => u.Tag).ToArray());
        }

        [Fact]
        public void LatestOnly_ReturnsSingleMax_NotLexical()
        {
            var result = CompositeContent.SelectUpdatesToInstall(
                Sample(), UpdateInstallStrategy.InstallLatestUpdateOnly, u => u.Ver);

            var only = Assert.Single(result);
            Assert.Equal("v0110", only.Tag);
        }

        [Fact]
        public void AllInOrder_EmptyIn_EmptyOut()
        {
            var result = CompositeContent.SelectUpdatesToInstall(
                Enumerable.Empty<Upd>(), UpdateInstallStrategy.InstallAllUpdatesInOrder, u => u.Ver);

            Assert.Empty(result);
        }

        [Fact]
        public void LatestOnly_EmptyIn_EmptyOut()
        {
            var result = CompositeContent.SelectUpdatesToInstall(
                Enumerable.Empty<Upd>(), UpdateInstallStrategy.InstallLatestUpdateOnly, u => u.Ver);

            Assert.Empty(result);
        }

        [Fact]
        public void NullIn_EmptyOut()
        {
            var result = CompositeContent.SelectUpdatesToInstall<Upd>(
                null, UpdateInstallStrategy.InstallAllUpdatesInOrder, u => u.Ver);

            Assert.Empty(result);
        }

        [Fact]
        public void LatestOnly_SingleElement_ReturnsIt()
        {
            var one = new[] { new Upd { Ver = Version.Parse("3.0"), Tag = "only" } };

            var result = CompositeContent.SelectUpdatesToInstall(
                one, UpdateInstallStrategy.InstallLatestUpdateOnly, u => u.Ver);

            Assert.Equal("only", Assert.Single(result).Tag);
        }

        [Fact]
        public void IntegerVersionKey_OrdersNumerically()
        {
            // Mirrors Yuzu's uint Version key (boxed through IComparable): 2 < 10 must hold.
            var updates = new[]
            {
                new KeyValuePair<uint, string>(10, "ten"),
                new KeyValuePair<uint, string>(2,  "two"),
            };

            var all = CompositeContent.SelectUpdatesToInstall(
                updates, UpdateInstallStrategy.InstallAllUpdatesInOrder, u => u.Key);
            Assert.Equal(new[] { "two", "ten" }, all.Select(u => u.Value).ToArray());

            var latest = CompositeContent.SelectUpdatesToInstall(
                updates, UpdateInstallStrategy.InstallLatestUpdateOnly, u => u.Key);
            Assert.Equal("ten", Assert.Single(latest).Value);
        }
    }
}
