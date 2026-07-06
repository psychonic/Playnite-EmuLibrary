using System;
using System.Linq;
using System.Threading;
using EmuLibrary.Installs;
using Playnite.SDK.Models;
using Xunit;

namespace EmuLibrary.Tests.Installs
{
    public class InstallProgressServiceTests
    {
        // Runs marshalled work inline so the service can be exercised without a WPF dispatcher.
        private sealed class SyncDispatcher : IUiDispatcher
        {
            public void Invoke(Action action) => action();
        }

        private static InstallProgressService NewService() =>
            new InstallProgressService(new SyncDispatcher(), _ => null, null);

        private static Game NewGame(string name = "Test Game") =>
            new Game(name) { Id = Guid.NewGuid() };

        [Fact]
        public void Begin_AddsActiveItem_NotFinished()
        {
            var svc = NewService();
            var game = NewGame();

            svc.Begin(game, new CancellationTokenSource());

            Assert.Single(svc.ViewModel.Active);
            Assert.Empty(svc.ViewModel.Finished);
            var item = svc.ViewModel.Active[0];
            Assert.Equal(game.Id, item.GameId);
            Assert.Equal(InstallStatus.Installing, item.Status);
            Assert.True(item.IsActive);
            Assert.True(svc.ViewModel.HasActive);
            Assert.False(svc.ViewModel.IsEmpty);
        }

        [Fact]
        public void Complete_MovesItemToFinished_WithCompletedStatus()
        {
            var svc = NewService();
            var handle = svc.Begin(NewGame(), new CancellationTokenSource());

            handle.Complete();

            Assert.Empty(svc.ViewModel.Active);
            var item = Assert.Single(svc.ViewModel.Finished);
            Assert.Equal(InstallStatus.Completed, item.Status);
            Assert.True(item.IsFinished);
            Assert.NotNull(item.FinishedOn);
            Assert.True(svc.ViewModel.HasFinished);
        }

        [Fact]
        public void Fail_MovesItemToFailedSection_NotFinished()
        {
            var svc = NewService();
            var handle = svc.Begin(NewGame(), new CancellationTokenSource());

            handle.Fail(new InvalidOperationException("boom"));

            var item = Assert.Single(svc.ViewModel.Failed);
            Assert.Empty(svc.ViewModel.Finished);
            Assert.Equal(InstallStatus.Failed, item.Status);
            Assert.Contains("boom", item.StatusText);
            Assert.True(svc.ViewModel.HasFailed);
            Assert.True(svc.ViewModel.HasClearable);
        }

        [Fact]
        public void Canceled_MovesItemToFinished_WithCanceledStatus()
        {
            var svc = NewService();
            var handle = svc.Begin(NewGame(), new CancellationTokenSource());

            handle.Canceled();

            var item = Assert.Single(svc.ViewModel.Finished);
            Assert.Equal(InstallStatus.Canceled, item.Status);
        }

        [Fact]
        public void TerminalTransition_IsIdempotent()
        {
            var svc = NewService();
            var handle = svc.Begin(NewGame(), new CancellationTokenSource());

            handle.Complete();
            handle.Fail(new Exception("late"));
            handle.Canceled();

            var item = Assert.Single(svc.ViewModel.Finished);
            Assert.Equal(InstallStatus.Completed, item.Status);
        }

        [Fact]
        public void Cancel_TripsRegisteredCts()
        {
            var svc = NewService();
            var game = NewGame();
            var cts = new CancellationTokenSource();
            svc.Begin(game, cts);

            svc.Cancel(game.Id);

            Assert.True(cts.IsCancellationRequested);
        }

        [Fact]
        public void Cancel_UnknownGame_IsNoOp()
        {
            var svc = NewService();
            var ex = Record.Exception(() => svc.Cancel(Guid.NewGuid()));
            Assert.Null(ex);
        }

        [Fact]
        public void ClearFinished_RemovesOnlyFinishedRow()
        {
            var svc = NewService();
            var finishedGame = NewGame("Finished");
            var activeGame = NewGame("Active");

            var finishedHandle = svc.Begin(finishedGame, new CancellationTokenSource());
            svc.Begin(activeGame, new CancellationTokenSource());
            finishedHandle.Complete();

            svc.ClearFinished(finishedGame.Id);

            Assert.Empty(svc.ViewModel.Finished);
            Assert.Single(svc.ViewModel.Active);
        }

        [Fact]
        public void ClearFinished_DoesNotRemoveActiveRow()
        {
            var svc = NewService();
            var game = NewGame();
            svc.Begin(game, new CancellationTokenSource());

            svc.ClearFinished(game.Id);

            Assert.Single(svc.ViewModel.Active);
        }

        [Fact]
        public void ClearAllFinished_RemovesFinished_KeepsActive()
        {
            var svc = NewService();
            var a = svc.Begin(NewGame("a"), new CancellationTokenSource());
            var b = svc.Begin(NewGame("b"), new CancellationTokenSource());
            svc.Begin(NewGame("c"), new CancellationTokenSource());
            a.Complete();
            b.Fail(new Exception());

            svc.ClearAllFinished();

            Assert.Empty(svc.ViewModel.Finished);
            Assert.Empty(svc.ViewModel.Failed);
            Assert.Single(svc.ViewModel.Active);
        }

        [Fact]
        public void Begin_Reinstall_DropsStaleFinishedRow()
        {
            var svc = NewService();
            var game = NewGame();

            svc.Begin(game, new CancellationTokenSource()).Complete();
            Assert.Single(svc.ViewModel.Finished);

            // Same game id installed again: the stale finished row is replaced by the new active one.
            svc.Begin(game, new CancellationTokenSource());

            Assert.Empty(svc.ViewModel.Finished);
            Assert.Single(svc.ViewModel.Active);
        }

        [Fact]
        public void SetBytes_RapidSubThresholdCalls_AreThrottled()
        {
            var svc = NewService();
            var handle = svc.Begin(NewGame(), new CancellationTokenSource());
            var item = svc.ViewModel.Active[0];

            int progressChanges = 0;
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(InstallProgressItem.ProgressValue))
                    progressChanges++;
            };

            const long total = 100 * 1024 * 1024;
            // Many rapid, small (sub-512 KiB, sub-250 ms) increments: only the first push and the final tick
            // should reach the UI; the rest are throttled out.
            for (long done = 1024; done < total; done += 4096)
                handle.SetBytes(done, total);
            handle.SetBytes(total, total);

            Assert.InRange(progressChanges, 1, 3);
            Assert.Equal(total, item.ProgressValue);
        }

        [Fact]
        public void SetBytes_ZeroTotal_FallsBackToIndeterminate()
        {
            var svc = NewService();
            var handle = svc.Begin(NewGame(), new CancellationTokenSource());
            var item = svc.ViewModel.Active[0];

            handle.SetStage("Copying…");
            handle.SetBytes(50, 0);

            Assert.True(item.IsIndeterminate);
        }

        // Phase 3: the auto-clear sweep removes aged finished rows when enabled.

        [Fact]
        public void SweepFinished_RemovesFinishedRowsOlderThanCutoff()
        {
            var svc = NewService();
            svc.Begin(NewGame(), new CancellationTokenSource()).Complete();
            var finishedOn = svc.ViewModel.Finished[0].FinishedOn.Value;

            // 6 minutes later, a 5-minute cutoff clears it.
            svc.SweepFinished(finishedOn.AddMinutes(6), enabled: true, minutes: 5);

            Assert.Empty(svc.ViewModel.Finished);
        }

        [Fact]
        public void SweepFinished_KeepsRowsYoungerThanCutoff()
        {
            var svc = NewService();
            svc.Begin(NewGame(), new CancellationTokenSource()).Complete();
            var finishedOn = svc.ViewModel.Finished[0].FinishedOn.Value;

            // Only 2 minutes old against a 5-minute cutoff: kept.
            svc.SweepFinished(finishedOn.AddMinutes(2), enabled: true, minutes: 5);

            Assert.Single(svc.ViewModel.Finished);
        }

        [Fact]
        public void SweepFinished_Disabled_IsANoOp()
        {
            var svc = NewService();
            svc.Begin(NewGame(), new CancellationTokenSource()).Complete();
            var finishedOn = svc.ViewModel.Finished[0].FinishedOn.Value;

            svc.SweepFinished(finishedOn.AddHours(1), enabled: false, minutes: 5);

            Assert.Single(svc.ViewModel.Finished);
        }

        [Fact]
        public void SweepFinished_NeverTouchesActiveInstalls()
        {
            var svc = NewService();
            svc.Begin(NewGame("active"), new CancellationTokenSource());

            svc.SweepFinished(DateTime.Now.AddHours(1), enabled: true, minutes: 5);

            Assert.Single(svc.ViewModel.Active);
            Assert.Empty(svc.ViewModel.Finished);
        }

        [Fact]
        public void SweepFinished_AlsoClearsAgedFailedRows()
        {
            var svc = NewService();
            svc.Begin(NewGame(), new CancellationTokenSource()).Fail(new Exception("boom"));
            var finishedOn = svc.ViewModel.Failed[0].FinishedOn.Value;

            svc.SweepFinished(finishedOn.AddMinutes(6), enabled: true, minutes: 5);

            Assert.Empty(svc.ViewModel.Failed);
        }

        [Fact]
        public void ClearFinished_RemovesAFailedRow()
        {
            var svc = NewService();
            var game = NewGame();
            svc.Begin(game, new CancellationTokenSource()).Fail(new Exception("boom"));

            svc.ClearFinished(game.Id);

            Assert.Empty(svc.ViewModel.Failed);
        }

        [Fact]
        public void PlayGame_InvokesInjectedLaunchAction()
        {
            Guid? launched = null;
            var svc = new InstallProgressService(new SyncDispatcher(), _ => null, null,
                startGame: id => launched = id, navigateToGame: null);

            var gameId = Guid.NewGuid();
            svc.PlayGame(gameId);

            Assert.Equal(gameId, launched);
        }

        [Fact]
        public void NavigateToGame_InvokesInjectedNavigateAction()
        {
            Guid? navigated = null;
            var svc = new InstallProgressService(new SyncDispatcher(), _ => null, null,
                startGame: null, navigateToGame: id => navigated = id);

            var gameId = Guid.NewGuid();
            svc.NavigateToGame(gameId);

            Assert.Equal(gameId, navigated);
        }
    }
}
