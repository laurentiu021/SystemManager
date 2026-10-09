// SysManager · HeldTimers — a clock whose timers fire only when the test says so
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire only when the test says so, so a test can reach "the time ran out"
/// without waiting for it. Hand-written, matching the fakes in <c>PingMonitorServiceLifecycleTests</c> and
/// <c>EtaCalculatorTests</c>.
/// </summary>
internal sealed class HeldTimers : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<(TimerCallback Callback, object? State, TimeSpan Due)> _timers = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate) _timers.Add((callback, state, dueTime));
        return new Held();
    }

    /// <summary>The due time of the one timer asked for.</summary>
    public TimeSpan OnlyDue
    {
        get { lock (_gate) return Assert.Single(_timers).Due; }
    }

    /// <summary>Fires every timer asked for so far, on the calling thread.</summary>
    public void FireAll()
    {
        List<(TimerCallback Callback, object? State, TimeSpan Due)> due;
        lock (_gate) due = [.. _timers];
        foreach (var timer in due) timer.Callback(timer.State);
    }

    private sealed class Held : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
