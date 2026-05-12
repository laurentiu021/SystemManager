// SysManager · EtaCalculator — estimates time remaining for progress-based operations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;

namespace SysManager.Helpers;

/// <summary>
/// Calculates estimated time remaining (ETA) for any operation that reports
/// progress as a percentage (0–100). Uses a simple linear extrapolation based
/// on elapsed time and current progress.
/// <para>
/// Usage: call <see cref="Reset"/> when the operation starts, then call
/// <see cref="Update"/> each time progress changes. Read <see cref="Remaining"/>
/// or <see cref="RemainingText"/> for the current estimate.
/// </para>
/// </summary>
public sealed class EtaCalculator
{
    private readonly Stopwatch _sw = new();
    private int _lastPercent;

    /// <summary>Estimated time remaining, or null if not enough data.</summary>
    public TimeSpan? Remaining { get; private set; }

    /// <summary>Human-readable ETA string (e.g. "~2 min 15 s" or "calculating…").</summary>
    public string RemainingText { get; private set; } = string.Empty;

    /// <summary>Resets the calculator. Call at the start of each operation.</summary>
    public void Reset()
    {
        _sw.Restart();
        _lastPercent = 0;
        Remaining = null;
        RemainingText = string.Empty;
    }

    /// <summary>
    /// Updates the estimate with the current progress percentage (0–100).
    /// Returns the formatted ETA string for convenience.
    /// </summary>
    public string Update(int percent)
    {
        _lastPercent = Math.Clamp(percent, 0, 100);

        if (_lastPercent <= 0 || !_sw.IsRunning)
        {
            Remaining = null;
            RemainingText = "calculating…";
            return RemainingText;
        }

        if (_lastPercent >= 100)
        {
            Remaining = TimeSpan.Zero;
            RemainingText = "done";
            return RemainingText;
        }

        // Linear extrapolation: total_time = elapsed / (percent / 100)
        // remaining = total_time - elapsed
        var elapsed = _sw.Elapsed;
        var totalEstimate = elapsed * (100.0 / _lastPercent);
        var remaining = totalEstimate - elapsed;

        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        Remaining = remaining;
        RemainingText = FormatTimeSpan(remaining);
        return RemainingText;
    }

    /// <summary>Formats a TimeSpan into a human-friendly short string.</summary>
    internal static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalSeconds < 5)
            return "a few seconds";
        if (ts.TotalSeconds < 60)
            return $"~{(int)ts.TotalSeconds} s";
        if (ts.TotalMinutes < 60)
        {
            var min = (int)ts.TotalMinutes;
            var sec = ts.Seconds;
            return sec > 0 ? $"~{min} min {sec} s" : $"~{min} min";
        }
        var hours = (int)ts.TotalHours;
        var mins = ts.Minutes;
        return mins > 0 ? $"~{hours} h {mins} min" : $"~{hours} h";
    }
}
