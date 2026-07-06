using System;
using System.Threading;
using EmuLibrary.Installs;
using Playnite.SDK.Models;
using Xunit;

namespace EmuLibrary.Tests.Installs
{
    // The sidebar item surfaces a title-level progress bar summed across the determinate active installs
    // (InstallsViewModel.AggregateProgress*). These pin that roll-up.
    public class InstallsAggregateProgressTests
    {
        private sealed class SyncDispatcher : IUiDispatcher
        {
            public void Invoke(Action action) => action();
        }

        private static InstallProgressService NewService() =>
            new InstallProgressService(new SyncDispatcher(), _ => null, null);

        private static Game NewGame(string name = "Game") => new Game(name) { Id = Guid.NewGuid() };

        [Fact]
        public void NoActiveInstalls_AggregateIsZero()
        {
            var svc = NewService();
            Assert.Equal(0, svc.ViewModel.AggregateProgressValue);
            Assert.Equal(0, svc.ViewModel.AggregateProgressMaximum);
        }

        [Fact]
        public void IndeterminateActiveInstall_ContributesNothing()
        {
            var svc = NewService();
            svc.Begin(NewGame(), new CancellationTokenSource()); // starts indeterminate ("Preparing…")

            Assert.Equal(0, svc.ViewModel.AggregateProgressMaximum);
        }

        [Fact]
        public void DeterminateInstalls_SumValuesAndMaxima()
        {
            var svc = NewService();
            var a = svc.Begin(NewGame("a"), new CancellationTokenSource());
            var b = svc.Begin(NewGame("b"), new CancellationTokenSource());

            a.SetBytes(30, 100);
            b.SetBytes(10, 50);

            Assert.Equal(40, svc.ViewModel.AggregateProgressValue);
            Assert.Equal(150, svc.ViewModel.AggregateProgressMaximum);
        }

        [Fact]
        public void CompletingAnInstall_RemovesItFromTheAggregate()
        {
            var svc = NewService();
            var a = svc.Begin(NewGame("a"), new CancellationTokenSource());
            var b = svc.Begin(NewGame("b"), new CancellationTokenSource());
            a.SetBytes(30, 100);
            b.SetBytes(10, 50);

            a.Complete(); // leaves the Active set

            Assert.Equal(10, svc.ViewModel.AggregateProgressValue);
            Assert.Equal(50, svc.ViewModel.AggregateProgressMaximum);
        }

        [Fact]
        public void AllInstallsFinished_AggregateReturnsToZero()
        {
            var svc = NewService();
            var a = svc.Begin(NewGame(), new CancellationTokenSource());
            a.SetBytes(30, 100);

            a.Complete();

            Assert.Equal(0, svc.ViewModel.AggregateProgressMaximum);
            Assert.Equal(0, svc.ViewModel.AggregateProgressValue);
        }
    }
}
