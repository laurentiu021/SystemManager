// SysManager · EtwBandwidthSourceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EtwBandwidthSource"/>'s bookkeeping — the parts that need no kernel ETW session.
/// <para>The session itself requires administrator and is refused cleanly when absent (<c>Start</c> returns
/// false), so these drive the accumulate path directly through the <c>internal</c> <c>Add</c> seam and read
/// results through <c>SampleAsync</c>, with an injected clock instead of real waiting. What is covered is
/// the PID-eviction contract added for #1816: before it, <c>_counters</c> was cleared only in
/// <c>Dispose</c>, so every PID the session ever saw stayed in a per-tick allocate-and-two-key-sort for the
/// tab's whole lifetime.</para>
/// <para>The COM/ETW subscription and the rate arithmetic itself are not covered here — the former needs a
/// real elevated session, the latter lives in <c>BandwidthFormat</c> and is tested in
/// <see cref="BandwidthFormatTests"/>.</para>
/// </summary>
public class EtwBandwidthSourceTests
{
    /// <summary>
    /// A clock that only moves when a test says so, overriding the same two members as
    /// <c>EtaCalculatorTests.TestTimeProvider</c>.
    /// </summary>
    /// <remarks>
    /// <c>GetTimestamp</c>, not <c>GetUtcNow</c>: the source needs a MONOTONIC clock, because it subtracts
    /// two readings both to compute rates and to age out PIDs, and a wall clock steps backwards on an NTP
    /// correction. This mattered in practice — the source shipped one release reading
    /// <c>GetUtcNow().UtcTicks</c>, and a stub overriding the wall clock could not have caught it, because
    /// a stub only ever moves forward, which is exactly the property the real wall clock lacks.
    /// </remarks>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static async Task<IReadOnlyList<int>> PidsAsync(EtwBandwidthSource src)
    {
        var snap = await src.SampleAsync();
        return [.. snap.Processes.Select(p => p.ProcessId)];
    }

    [Fact]
    public async Task Sample_ReportsAPidThatTransferredBytes()
    {
        var clock = new TestClock();
        using var src = new EtwBandwidthSource(clock);

        src.Add(1234, down: 5_000, up: 0, "chrome.exe");

        Assert.Equal([1234], await PidsAsync(src));
    }

    /// <summary>
    /// A PID silent past the eviction window is dropped, so the per-tick cost tracks what is CURRENTLY
    /// active rather than everything the session has ever seen.
    /// </summary>
    [Fact]
    public async Task Sample_EvictsAPidIdlePastTheWindow()
    {
        var clock = new TestClock();
        using var src = new EtwBandwidthSource(clock);
        src.Add(1234, down: 5_000, up: 0, "chrome.exe");

        // One sample inside the window: still present.
        Assert.Equal([1234], await PidsAsync(src));

        clock.Advance(TimeSpan.FromMinutes(11));

        // The sample on which it ages out still lists it — eviction runs after the snapshot is built, so a
        // row does not vanish from under a user mid-glance…
        Assert.Equal([1234], await PidsAsync(src));

        // …and the next one no longer does.
        Assert.Empty(await PidsAsync(src));
    }

    /// <summary>
    /// Eviction measures INACTIVITY, not age. A long-lived process that keeps transferring must survive
    /// indefinitely — a cutoff against creation time would drop exactly the busiest apps.
    /// </summary>
    [Fact]
    public async Task Sample_KeepsAPidThatIsStillTransferring_HoweverOld()
    {
        var clock = new TestClock();
        using var src = new EtwBandwidthSource(clock);
        src.Add(1234, down: 1_000, up: 0, "chrome.exe");

        // Well past the window in total, but never silent for a whole one.
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(9));
            src.Add(1234, down: 1_000, up: 0, "chrome.exe");
            await src.SampleAsync();
        }

        // Asserted on the SESSION TOTAL, not on the PID being present, and that distinction was found by
        // mutation. Freezing the activity stamp at first-seen — the plausible wrong implementation — does
        // evict this PID mid-run, but the very next event re-adds it through GetOrAdd with a fresh
        // counter, so "is 1234 in the list" is green either way. What the user actually loses is the
        // running total for an app that never stopped transferring: five events of 1,000 bytes must read
        // as 5,000, not as however much arrived since a silent eviction reset it.
        var row = Assert.Single((await src.SampleAsync()).Processes);
        Assert.Equal(1234, row.ProcessId);
        Assert.Equal(5_000, row.TotalDownBytes);
    }

    [Fact]
    public async Task Sample_EvictsOnlyTheIdlePid_NotItsActiveNeighbour()
    {
        var clock = new TestClock();
        using var src = new EtwBandwidthSource(clock);
        src.Add(1111, down: 1_000, up: 0, "idle.exe");
        src.Add(2222, down: 1_000, up: 0, "busy.exe");

        clock.Advance(TimeSpan.FromMinutes(11));
        src.Add(2222, down: 1_000, up: 0, "busy.exe");   // only this one is still going

        await src.SampleAsync();                          // the pass that ages 1111 out
        Assert.Equal([2222], await PidsAsync(src));
    }

    [Fact]
    public async Task Add_IgnoresPidZero_AndNegatives()
    {
        // The idle/system pseudo-process carries kernel traffic that belongs to no app the user can act
        // on, and a negative id is not a PID at all.
        var clock = new TestClock();
        using var src = new EtwBandwidthSource(clock);

        src.Add(0, down: 9_999, up: 9_999, "Idle");
        src.Add(-1, down: 9_999, up: 9_999, "?");

        Assert.Empty(await PidsAsync(src));
    }

    [Fact]
    public void Start_WithoutAdministrator_RefusesCleanly()
    {
        // The whole fallback story depends on this returning false rather than throwing: the ViewModel
        // reads IsAvailable and silently uses the no-admin source instead.
        var clock = new TestClock();
        using var src = new EtwBandwidthSource(clock);

        if (Helpers.AdminHelper.IsElevated()) return;   // elevated CI runner: the negative path is moot

        Assert.False(src.Start());
        Assert.False(src.IsAvailable);
    }
}
