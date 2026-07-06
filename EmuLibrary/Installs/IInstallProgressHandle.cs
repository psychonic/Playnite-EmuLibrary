using System;

namespace EmuLibrary.Installs
{
    // Handed to an install controller for the duration of one install. The controller reports progress
    // through it; the handle marshals updates onto the UI thread and throttles quantitative updates (by
    // bytes and time) so a tight copy/decrypt loop can't flood the dispatcher. Terminal calls
    // (Complete/Fail/Canceled) move the item to the finished section. Safe to call from any thread.
    internal interface IInstallProgressHandle
    {
        // Indeterminate stage label, e.g. "Installing base…", "Decrypting…".
        void SetStage(string text);

        // Alias for SetStage kept for readability at call sites that emphasize the indeterminate bar.
        void SetIndeterminate(string text);

        // Quantitative progress. total <= 0 falls back to indeterminate. Throttled.
        void SetBytes(long done, long total);

        // Quantitative progress as a 0..100 percentage. Throttled.
        void SetPercent(double pct);

        void Complete();
        void Fail(Exception ex);
        void Canceled();
    }
}
