using Playnite.SDK.Models;
using System;
using System.Threading;

namespace EmuLibrary.Installs
{
    // Singleton read model the install controllers push status into and the Installs panel renders.
    // Exposed on IEmuLibrary. The panel only observes; it never triggers installs (Playnite owns the
    // InstallController lifecycle — the panel observes, it does not re-queue).
    internal interface IInstallProgressService
    {
        InstallsViewModel ViewModel { get; }

        // Registers a new in-flight install and returns the handle the controller reports through. The CTS
        // is the controller's _watcherToken; Cancel(gameId) trips it.
        IInstallProgressHandle Begin(Game game, CancellationTokenSource cts);

        // Gracefully cancels the install for gameId by tripping its registered CTS (no-op if unknown).
        void Cancel(Guid gameId);

        // Removes one terminal (finished or failed) row (no-op if active or unknown).
        void ClearFinished(Guid gameId);

        // Removes all terminal (finished + failed) rows, leaving active installs untouched.
        void ClearAllFinished();

        // Launches the game via Playnite (used by the Play button on a completed row).
        void PlayGame(Guid gameId);

        // Switches to the library view and selects the game (used by clicking a row).
        void NavigateToGame(Guid gameId);
    }
}
