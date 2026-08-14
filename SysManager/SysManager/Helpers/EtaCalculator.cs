// SysManager · EtaCalculator — estimates time remaining for progress-based operations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// Calculates estimated time remaining (ETA) for any operation that reports progress as a percentage
/// (0–100), from a smoothed estimate of how fast progress is actually moving.
/// <para>
/// Usage: call <see cref="Reset"/> when the operation starts, then call <see cref="Update"/> each time
/// progress changes. Read <see cref="Remaining"/> or <see cref="RemainingText"/> for the current
/// estimate — both are evaluated when read, so they keep counting down between progress reports rather
/// than holding whatever the last report produced.
/// </para>
/// <para>
/// This used to extrapolate from the average since the start (<c>elapsed * 100 / percent</c>), computed
/// only when a caller reported progress. Three consequences, all measured against the old formula:
/// callers derive percent by integer division, so during one long step the SAME percent arrives
/// repeatedly — and the estimate GREW with elapsed time, climbing from "~1 min" to "~37 min" while the
/// bar never moved; between reports the text froze on a number that was already stale; and a slow first
/// step displayed "~1.6 h" moments before the real answer turned out to be 90 s.
/// </para>
/// <para>
/// Thread safety: this class is NOT thread-safe. All calls must be made from the same thread (typically
/// the UI thread via Progress&lt;T&gt; callbacks).
/// </para>
/// </summary>
public sealed class EtaCalculator
{
    /// <summary>
    /// Weight given to the newest rate sample. 0.3 stays responsive to a genuine slowdown within a few
    /// samples while ignoring the jitter of one slow step — the usual low-pass constant for transfer
    /// dialogs. Higher jumps around; lower reacts too late to be useful.
    /// </summary>
    private const double RateSmoothing = 0.3;

    /// <summary>
    /// No estimate is shown below this mark. Early samples are dominated by start-up cost — opening
    /// handles, enumerating a tree — and extrapolating from them is what produced the "~1.6 h" that
    /// became 90 s. "calculating…" is more honest than a number certain to be wrong.
    /// </summary>
    private const int MinPercentForEstimate = 3;

    private readonly TimeProvider _time;

    private long _startTimestamp;
    private long _lastSampleTimestamp;
    private bool _started;
    private bool _completed;
    private int _lastPercent;

    /// <summary>Percent per second, exponentially smoothed. Zero until a real advance has been seen.</summary>
    private double _rate;

    /// <summary>Elapsed-since-start at which progress is projected to reach 100%. Null when unknown.</summary>
    private TimeSpan? _projectedFinish;

    /// <param name="timeProvider">
    /// Time source, defaulting to <see cref="TimeProvider.System"/>. A test passes a fake and advances it,
    /// so the ETA maths is verified deterministically. This class held a private <c>Stopwatch</c> before,
    /// which is precisely why its tests used <c>Thread.Sleep</c> — the non-deterministic pattern the
    /// project's testing discipline rules out.
    /// </param>
    public EtaCalculator(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Estimated time remaining, or null when there is not enough data yet.
    /// <para>Derived at read time from the projected finish, so an operation that has gone quiet shows
    /// its remaining time running down instead of a frozen value.</para>
    /// </summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (_completed) return TimeSpan.Zero;
            if (_projectedFinish is not { } finish) return null;
            var left = finish - Elapsed;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>Human-readable ETA string (e.g. "~2 min 15 s", "calculating…", "done").</summary>
    public string RemainingText
    {
        get
        {
            if (_completed) return "done";
            if (!_started) return string.Empty;
            return Remaining is { } left ? FormatTimeSpan(left) : "calculating…";
        }
    }

    private TimeSpan Elapsed => _started ? _time.GetElapsedTime(_startTimestamp) : TimeSpan.Zero;

    /// <summary>Resets the calculator. Call at the start of each operation.</summary>
    public void Reset()
    {
        _startTimestamp = _time.GetTimestamp();
        _lastSampleTimestamp = _startTimestamp;
        _started = true;
        _completed = false;
        _lastPercent = 0;
        _rate = 0;
        _projectedFinish = null;
    }

    /// <summary>
    /// Updates the estimate with the current progress percentage (0–100). Returns the formatted ETA
    /// string for convenience.
    /// </summary>
    public string Update(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);

        // Update before Reset: report honestly rather than measuring from an unset start.
        if (!_started)
        {
            _lastPercent = clamped;
            return "calculating…";
        }

        if (clamped >= 100)
        {
            _lastPercent = 100;
            _completed = true;
            _projectedFinish = null;
            return RemainingText;
        }

        _completed = false;

        var advanced = clamped - _lastPercent;
        var sinceLastSample = _time.GetElapsedTime(_lastSampleTimestamp);

        // A repeated or backwards percent carries no rate information — and re-projecting on one is the
        // whole bug in a different costume. `Elapsed + secondsLeft` would push the finish forward by
        // exactly the time that just passed, so the displayed value would never move (the first draft of
        // this fix did that, and the stall test caught it). Returning early leaves the existing
        // projection in place, and because Remaining is derived at read time, it counts down on its own.
        if (advanced <= 0 || sinceLastSample <= TimeSpan.Zero)
        {
            _lastPercent = clamped;
            return RemainingText;
        }

        var sample = advanced / sinceLastSample.TotalSeconds;
        _rate = _rate <= 0 ? sample : (RateSmoothing * sample) + ((1 - RateSmoothing) * _rate);
        _lastSampleTimestamp = _time.GetTimestamp();
        _lastPercent = clamped;

        if (clamped < MinPercentForEstimate || _rate <= 0)
        {
            _projectedFinish = null;
            return RemainingText;
        }

        _projectedFinish = Elapsed + TimeSpan.FromSeconds((100 - clamped) / _rate);
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
