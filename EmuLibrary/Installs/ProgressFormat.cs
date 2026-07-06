using System;

namespace EmuLibrary.Installs
{
    // Formats the quantitative status line shown on a progress row: "<stage> <pct>% · <rate> · <eta> left".
    // Rate/ETA are appended only when a rate is known (null before the estimator warms up, or for a stage that
    // reports no bytes). Pure/stateless so it's unit-testable.
    internal static class ProgressFormat
    {
        // Builds the full status line. stage is the current phase label (already stripped of a trailing
        // ellipsis by StageLabel); bytesPerSec is the smoothed rate or null.
        public static string Line(string stage, long done, long total, double? bytesPerSec)
        {
            var pct = total > 0 ? done * 100.0 / total : 0;
            var text = $"{stage} {pct:0}%";

            if (bytesPerSec.HasValue && bytesPerSec.Value > 0)
            {
                text += $" · {Rate(bytesPerSec.Value)}";
                if (total > done)
                    text += $" · {Eta((total - done) / bytesPerSec.Value)} left";
            }

            return text;
        }

        // Normalizes a stage label for use as a prefix: trims the trailing "…"/"." and whitespace so appending
        // a percentage reads cleanly ("Installing base…" -> "Installing base"). Blank falls back to "Installing".
        public static string StageLabel(string stage)
        {
            var trimmed = stage?.TrimEnd('…', '.', ' ');
            return string.IsNullOrEmpty(trimmed) ? "Installing" : trimmed;
        }

        // Human-readable transfer rate: "0.9 KB/s", "18.4 MB/s", "1.2 GB/s".
        public static string Rate(double bytesPerSec)
        {
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
            var value = bytesPerSec;
            var i = 0;
            while (value >= 1024 && i < units.Length - 1)
            {
                value /= 1024;
                i++;
            }
            return $"{value:0.#} {units[i]}";
        }

        // Compact remaining-time text: "42s", "3m 20s", "1h 04m".
        public static string Eta(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 1)
                return "0s";

            var ts = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes:00}m";
            if (ts.TotalMinutes >= 1)
                return $"{ts.Minutes}m {ts.Seconds:00}s";
            return $"{ts.Seconds}s";
        }
    }
}
