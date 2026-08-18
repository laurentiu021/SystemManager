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
/// Thread safety: every public member is serialized on an internal lock, so callers may report progress
/// from whatever thread it reaches them on. That is not a convenience — <c>CleanupViewModel</c> feeds this
/// from <c>PowerShellRunner.LineReceived</c>, raised on the <c>OutputDataReceived</c> and
/// <c>ErrorDataReceived</c> threadpool threads with nothing marshalling in between, and the previous
/// "callers must be single-threaded" contract was simply not being met.
/// </para>
/// </summary>
public sealed class EtaCalculator
{
    /// <summary>
    /// Time constant of the rate smoother, in seconds. The weight given to a sample is
    /// <c>1 - e^(-Δt / τ)</c>, so it grows with the interval the sample actually covers instead of being
    /// the same for every report.
    /// <para>2.8 s is chosen so nothing changes at the cadence callers really report at: at Δt = 1 s the
    /// weight is <c>1 - e^(-1/2.8) = 0.30</c>, exactly the fixed 0.3 this replaced. It stays responsive to
    /// a genuine slowdown within a few samples while ignoring the jitter of one slow step.</para>
    /// <para>The fixed weight was wrong in both directions. A sample spanning a 10-minute stall got the
    /// same 0.3 as a sample spanning one second, so the resuming rate — the only honest measurement
    /// available — was outvoted 7:3 by a pace that no longer existed: 1%/s established, 600 s stall, then
    /// 1% more read as "~2 min" when the observed pace implied hours. And a sub-millisecond interval made
    /// <c>advance / Δt</c> explode, which the fixed weight passed straight through. Time-weighting bounds
    /// that: the contribution is <c>(Δt/τ)·(advance/Δt) = advance/τ</c>, independent of how small Δt
    /// is.</para>
    /// </summary>
    private const double RateTimeConstantSeconds = 2.8;

    /// <summary>
    /// Shortest interval accepted as a rate sample. Below it the advance is CARRIED — neither the percent
    /// nor the sample timestamp moves — so it is measured over the real interval on the next report rather
    /// than over a microsecond one. Carrying rather than discarding is what makes a floor safe at any
    /// speed: a fast operation reporting every 100 ms simply coalesces into ~250 ms windows, and the ratio
    /// the rate is computed from is unchanged.
    /// <para>Needed because progress does not arrive on a timer. <c>sfc</c> and DISM emit their redraws
    /// through <c>Process.OutputDataReceived</c>, which flushes buffered lines back-to-back, so two
    /// different percents can be microseconds apart. Rejecting only a ZERO interval was not enough:
    /// 1% in 50 µs is 20,000 %/s.</para>
    /// </summary>
    private static readonly TimeSpan MinSampleInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Fraction of the projected duration an operation may overrun before the text stops claiming it is
    /// nearly done. Proportional, with <see cref="MinOverdueGrace"/> as a floor, because 30 s past a
    /// "few seconds" estimate is a broken promise while 30 s past a two-hour estimate is noise.
    /// </summary>
    private const double OverdueFraction = 0.1;

    /// <summary>Lower bound on the overdue grace, so a short projection is not called stale immediately.</summary>
    private static readonly TimeSpan MinOverdueGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// No estimate is shown below this mark. Early samples are dominated by start-up cost — opening
    /// handles, enumerating a tree — and extrapolating from them is what produced the "~1.6 h" that
    /// became 90 s. "calculating…" is more honest than a number certain to be wrong.
    /// </summary>
    private const int MinPercentForEstimate = 3;

    private readonly TimeProvider _time;

    /// <summary>
    /// Serializes every read and write of the fields below. Cheap (uncontended in the common case) and it
    /// removes a real hazard rather than a theoretical one: <c>CleanupViewModel</c> calls
    /// <see cref="Update"/> straight from <c>PowerShellRunner.LineReceived</c>, which is raised on
    /// <c>Process.OutputDataReceived</c> AND <c>ErrorDataReceived</c> — two threadpool threads into one
    /// handler, with nothing marshalling to the UI thread. <c>_projectedFinish</c> is a
    /// <c>TimeSpan?</c>, so a torn read there yields an arbitrary duration on screen.
    /// </summary>
    private readonly object _gate = new();

    private long _startTimestamp;
    private long _lastSampleTimestamp;
    private bool _started;
    private bool _completed;
    private int _lastPercent;

    /// <summary>Percent per second, exponentially smoothed. Zero until a real advance has been seen.</summary>
    private double _rate;

    /// <summary>Elapsed-since-start at which progress is projected to reach 100%. Null when unknown.</summary>
    private TimeSpan? _projectedFinish;

    /// <summary>
    /// Creates a calculator holding no samples: <see cref="Remaining"/> stays null and the rate stays zero
    /// until the first <c>Update</c> reports a real advance.
    /// </summary>
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
        get { lock (_gate) return RemainingLocked; }
    }

    /// <summary>Body of <see cref="Remaining"/>. Caller holds <see cref="_gate"/>.</summary>
    private TimeSpan? RemainingLocked
    {
        get
        {
            if (_completed) return TimeSpan.Zero;
            if (_projectedFinish is not { } finish) return null;
            var left = finish - Elapsed;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>
    /// True once the operation has run measurably past its projected finish — the projection has expired
    /// and the calculator no longer has a usable estimate.
    /// <para>Separated from "almost done" deliberately. <see cref="Remaining"/> clamps at zero, so a run
    /// that overshoots its projection and then hangs is indistinguishable from one about to finish: an
    /// operation projected to end at 100 s that stalls at 40% reported "a few seconds" for the following
    /// 25 minutes. Beyond the grace, <see cref="RemainingText"/> says so instead of promising.</para>
    /// </summary>
    private bool IsOverdue
    {
        get
        {
            if (_completed || _projectedFinish is not { } finish) return false;
            var overrun = Elapsed - finish;
            if (overrun <= TimeSpan.Zero) return false;
            var grace = finish * OverdueFraction;
            return overrun > (grace > MinOverdueGrace ? grace : MinOverdueGrace);
        }
    }

    /// <summary>
    /// Human-readable ETA string (e.g. "~2 min 15 s", "calculating…", "taking longer than expected",
    /// "done").
    /// </summary>
    public string RemainingText
    {
        get { lock (_gate) return RemainingTextLocked; }
    }

    /// <summary>Body of <see cref="RemainingText"/>. Caller holds <see cref="_gate"/>.</summary>
    private string RemainingTextLocked
    {
        get
        {
            if (_completed) return "done";
            if (!_started) return string.Empty;
            if (IsOverdue) return "taking longer than expected";
            return RemainingLocked is { } left ? FormatTimeSpan(left) : "calculating…";
        }
    }

    private TimeSpan Elapsed => _started ? _time.GetElapsedTime(_startTimestamp) : TimeSpan.Zero;

    /// <summary>Resets the calculator. Call at the start of each operation.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _startTimestamp = _time.GetTimestamp();
            _lastSampleTimestamp = _startTimestamp;
            _started = true;
            _completed = false;
            _lastPercent = 0;
            _rate = 0;
            _projectedFinish = null;
        }
    }

    /// <summary>
    /// Updates the estimate with the current progress percentage (0–100). Returns the formatted ETA
    /// string for convenience.
    /// </summary>
    public string Update(int percent)
    {
        lock (_gate) return UpdateLocked(percent);
    }

    /// <summary>Body of <see cref="Update"/>. Caller holds <see cref="_gate"/>.</summary>
    private string UpdateLocked(int percent)
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
            return RemainingTextLocked;
        }

        _completed = false;

        var advanced = clamped - _lastPercent;
        var sinceLastSample = _time.GetElapsedTime(_lastSampleTimestamp);

        // A repeated or backwards percent carries no rate information — and re-projecting on one is the
        // whole bug in a different costume. `Elapsed + secondsLeft` would push the finish forward by
        // exactly the time that just passed, so the displayed value would never move (the first draft of
        // this fix did that, and the stall test caught it). Returning early leaves the existing
        // projection in place, and because Remaining is derived at read time, it counts down on its own.
        if (advanced <= 0)
        {
            _lastPercent = clamped;
            return RemainingTextLocked;
        }

        // Too soon to measure a rate from. Leave BOTH _lastPercent and _lastSampleTimestamp alone so the
        // advance is carried into the next report and divided by the real interval — dividing it by a
        // microsecond is what produced 20,000 %/s. Discarding the advance instead would lose progress
        // permanently for a caller that reports faster than the floor.
        if (sinceLastSample < MinSampleInterval) return RemainingTextLocked;

        var sample = advanced / sinceLastSample.TotalSeconds;

        // Weight by the interval the sample covers, not per call: a sample spanning a long gap IS the
        // better measurement and must dominate, and a short one must not overturn an established rate.
        var weight = 1 - Math.Exp(-sinceLastSample.TotalSeconds / RateTimeConstantSeconds);
        _rate = _rate <= 0 ? sample : (weight * sample) + ((1 - weight) * _rate);
        _lastSampleTimestamp = _time.GetTimestamp();
        _lastPercent = clamped;

        if (clamped < MinPercentForEstimate || _rate <= 0)
        {
            _projectedFinish = null;
            return RemainingTextLocked;
        }

        _projectedFinish = Elapsed + TimeSpan.FromSeconds((100 - clamped) / _rate);
        return RemainingTextLocked;
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
