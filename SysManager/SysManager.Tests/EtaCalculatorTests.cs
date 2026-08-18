// SysManager · EtaCalculatorTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EtaCalculator"/>.
/// <para>Every timing assertion drives an injected <see cref="TestTimeProvider"/> instead of sleeping.
/// The two tests that once called <c>Thread.Sleep</c> could only assert "some estimate came back" —
/// they could not check the number, which is why the estimate could grow while the progress bar stood
/// still and nothing failed. With a controlled clock the arithmetic itself is pinned.</para>
/// </summary>
public class EtaCalculatorTests
{
    /// <summary>
    /// A <see cref="TimeProvider"/> whose clock only moves when a test says so. Hand-written rather than
    /// taking a dependency on Microsoft.Extensions.TimeProvider.Testing: the calculator needs only
    /// <c>GetTimestamp</c> and <c>GetElapsedTime</c>, and <c>GetElapsedTime</c> derives from
    /// <c>TimestampFrequency</c>, so overriding those two is the whole seam.
    /// </summary>
    private sealed class TestTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var eta = new EtaCalculator(new TestTimeProvider());
        eta.Reset();
        Assert.Null(eta.Remaining);
        Assert.Equal("calculating…", eta.RemainingText);
    }

    [Fact]
    public void Update_AtZeroPercent_ReturnsCalculating()
    {
        var eta = new EtaCalculator(new TestTimeProvider());
        eta.Reset();
        var text = eta.Update(0);
        Assert.Equal("calculating…", text);
        Assert.Null(eta.Remaining);
    }

    [Fact]
    public void Update_At100Percent_ReturnsDone()
    {
        var eta = new EtaCalculator(new TestTimeProvider());
        eta.Reset();
        var text = eta.Update(100);
        Assert.Equal("done", text);
        Assert.Equal(TimeSpan.Zero, eta.Remaining);
    }

    [Fact]
    public void Update_ClampsAbove100()
    {
        var eta = new EtaCalculator(new TestTimeProvider());
        eta.Reset();
        Assert.Equal("done", eta.Update(150));
    }

    [Fact]
    public void Update_ClampsBelow0()
    {
        var eta = new EtaCalculator(new TestTimeProvider());
        eta.Reset();
        Assert.Equal("calculating…", eta.Update(-5));
    }

    [Theory]
    [InlineData(3, "a few seconds")]
    [InlineData(30, "~30 s")]
    [InlineData(90, "~1 min 30 s")]
    [InlineData(3600, "~1 h")]
    [InlineData(3660, "~1 h 1 min")]
    public void FormatTimeSpan_FormatsCorrectly(int seconds, string expected)
    {
        Assert.Equal(expected, EtaCalculator.FormatTimeSpan(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Update_WithoutReset_ReturnsCalculating()
    {
        var eta = new EtaCalculator(new TestTimeProvider());
        // No Reset() — there is no start instant to measure from.
        Assert.Equal("calculating…", eta.Update(50));
    }

    /// <summary>A steady 1%/s run must produce exactly the seconds remaining, not an approximation of it.</summary>
    [Fact]
    public void Update_AtSteadyRate_ProjectsTheExactRemainingTime()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        // 10 s to reach 10% => 1%/s => 90% left => 90 s.
        time.Advance(TimeSpan.FromSeconds(10));
        var text = eta.Update(10);

        Assert.Equal(TimeSpan.FromSeconds(90), eta.Remaining);
        Assert.Equal("~1 min 30 s", text);
    }

    /// <summary>
    /// The regression that motivated this rewrite. Callers derive percent by integer division, so during
    /// one long step the SAME percent arrives repeatedly. Under the old average-since-start formula
    /// (<c>elapsed * (100 - p) / p</c>) the estimate GREW every time: measured at 12%, it went 1.2 min →
    /// 3.7 → 7.3 → 14.7 → 36.7 min while the bar never moved. The remaining time must fall.
    /// </summary>
    [Fact]
    public void Update_RepeatedAtTheSamePercent_CountsDownInsteadOfGrowing()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        time.Advance(TimeSpan.FromSeconds(10));
        eta.Update(10);
        var first = eta.Remaining!.Value;

        var previous = first;
        foreach (var _ in Enumerable.Range(0, 4))
        {
            time.Advance(TimeSpan.FromSeconds(20));
            eta.Update(10);   // stalled: identical percent
            var now = eta.Remaining!.Value;
            Assert.True(now < previous,
                $"remaining grew from {previous} to {now} while progress stood still — the stall bug is back.");
            previous = now;
        }

        Assert.True(previous < first - TimeSpan.FromSeconds(60),
            $"after 80 s of stall the estimate only moved from {first} to {previous}.");
    }

    /// <summary>
    /// Reading between progress reports must count down, not hold the last value. A stalled operation
    /// showing a frozen "~10 s" for minutes is the visible half of the same defect.
    /// </summary>
    [Fact]
    public void Remaining_BetweenUpdates_KeepsCountingDown()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        time.Advance(TimeSpan.FromSeconds(10));
        eta.Update(10);
        Assert.Equal(TimeSpan.FromSeconds(90), eta.Remaining);

        // No Update at all — only the clock moves.
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(60), eta.Remaining);

        time.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(TimeSpan.Zero, eta.Remaining);   // clamped, never negative
    }

    /// <summary>
    /// A slow first step must not be extrapolated. Under the old formula, 1% after 60 s displayed
    /// "~1 h 39 min" for an operation that finished in 90 s.
    /// </summary>
    [Fact]
    public void Update_BelowTheEstimateFloor_SaysCalculatingRatherThanGuessing()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("calculating…", eta.Update(1));
        Assert.Null(eta.Remaining);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("calculating…", eta.Update(2));
        Assert.Null(eta.Remaining);

        // Past the floor, an estimate appears — and it reflects the RECENT rate, not the slow start.
        time.Advance(TimeSpan.FromSeconds(1));
        eta.Update(10);
        Assert.NotNull(eta.Remaining);
        Assert.True(eta.Remaining < TimeSpan.FromMinutes(10),
            $"the slow first step is still dominating the estimate: {eta.Remaining}.");
    }

    /// <summary>A sustained slowdown must be reflected, or the estimate is just the opening rate forever.</summary>
    [Fact]
    public void Update_WhenProgressSlowsDown_TheEstimateGrowsToMatch()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        // Fast opening: 10%/s.
        for (var p = 10; p <= 40; p += 10)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            eta.Update(p);
        }
        var fast = eta.Remaining!.Value;

        // Then a tenth of that pace, sustained.
        for (var p = 50; p <= 70; p += 10)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            eta.Update(p);
        }
        var slow = eta.Remaining!.Value;

        Assert.True(slow > fast,
            $"progress slowed 10x but the estimate fell from {fast} to {slow} — the rate is not being tracked.");
    }

    [Fact]
    public void MultipleResets_WorkCorrectly()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();
        time.Advance(TimeSpan.FromSeconds(5));
        eta.Update(50);
        Assert.NotNull(eta.Remaining);

        eta.Reset();
        Assert.Null(eta.Remaining);
        Assert.Equal("calculating…", eta.RemainingText);
    }

    /// <summary>
    /// The default constructor must still use the real clock, so the six view models that call
    /// <c>new EtaCalculator()</c> keep working untouched.
    /// </summary>
    [Fact]
    public void DefaultConstructor_UsesTheSystemClock()
    {
        var eta = new EtaCalculator();
        eta.Reset();
        Assert.Equal("calculating…", eta.RemainingText);
        Assert.Equal("done", eta.Update(100));
    }

    /// <summary>
    /// Progress does not arrive on a timer. <c>sfc</c> and DISM emit their redraws through
    /// <c>Process.OutputDataReceived</c>, which flushes buffered lines back-to-back, so two different
    /// percents can be microseconds apart. Dividing an advance by a microsecond yields thousands of
    /// percent per second, and one such sample used to pin the display at "a few seconds" for the rest of
    /// the run: it takes ~29 further forward samples for a 0.3-weighted average to work 6000 %/s back down,
    /// more than SFC emits in the remainder of a scan.
    /// </summary>
    [Fact]
    public void Update_WhenTwoPercentsArriveMicrosecondsApart_DoesNotBelieveTheImpliedRate()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        // Establish an honest 0.1 %/s: 10% in 100 s => 90% left => ~900 s.
        time.Advance(TimeSpan.FromSeconds(100));
        eta.Update(10);
        var honest = eta.Remaining!.Value;
        Assert.True(honest > TimeSpan.FromMinutes(10), $"the baseline estimate is already wrong: {honest}.");

        // Now the flush: 11% and 12% land 50 microseconds apart. Naively that is 20,000 %/s.
        time.Advance(TimeSpan.FromMilliseconds(0.05));
        eta.Update(11);
        time.Advance(TimeSpan.FromMilliseconds(0.05));
        eta.Update(12);

        Assert.NotEqual("a few seconds", eta.RemainingText);
        Assert.True(eta.Remaining!.Value > TimeSpan.FromMinutes(5),
            $"a 50 µs sample collapsed the estimate to {eta.Remaining} — the burst is being taken as a rate.");
    }

    /// <summary>
    /// The advance under the sample floor must be CARRIED, not discarded: an operation reporting faster
    /// than the floor must still be measured, just over a coalesced window. Discarding it instead would
    /// lose progress permanently and hold the ETA at the opening rate forever.
    /// </summary>
    [Fact]
    public void Update_BelowTheSampleFloor_CarriesTheAdvanceIntoTheNextSample()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        // Ten reports at 100 ms each: every one is under the 250 ms floor on its own, but together they
        // span 1 s and 10% of progress — an honest 10 %/s, so 90% left is ~9 s.
        for (var p = 1; p <= 10; p++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            eta.Update(p);
        }

        var remaining = eta.Remaining;
        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// A sample spanning a long stall IS the better measurement and must dominate. Under the old fixed
    /// 0.3 weight the resuming rate was outvoted 7:3 by a pace that no longer existed, so a 10-minute
    /// stall followed by 1% of progress reported "~2 min" when the observed pace implied hours.
    /// </summary>
    [Fact]
    public void Update_AfterALongStall_TrustsTheResumingRateOverThePreStallPace()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        // 1 %/s established over ten seconds.
        for (var p = 1; p <= 10; p++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            eta.Update(p);
        }

        // Ten minutes of silence, then one more percent. Observed pace: 1% per 600 s => 89% => ~14.8 h.
        time.Advance(TimeSpan.FromMinutes(10));
        eta.Update(11);

        Assert.True(eta.Remaining!.Value > TimeSpan.FromHours(1),
            $"after a 10-minute stall the estimate is {eta.Remaining} — the pre-stall rate still dominates.");
    }

    /// <summary>
    /// Past its projected finish the text must stop claiming the operation is nearly done. Remaining
    /// clamps at zero, so an operation that overshoots and then hangs is otherwise indistinguishable from
    /// one about to complete — it showed "a few seconds" for as long as the hang lasted.
    /// </summary>
    [Fact]
    public void RemainingText_WellPastTheProjectedFinish_SaysItIsTakingLongerRatherThanAlmostDone()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);
        eta.Reset();

        // 1 %/s at 10% => projected finish at t = 100 s.
        time.Advance(TimeSpan.FromSeconds(10));
        eta.Update(10);

        // Just past the projection, still inside the grace: "a few seconds" is fair, it may be finishing.
        time.Advance(TimeSpan.FromSeconds(95));
        Assert.Equal(TimeSpan.Zero, eta.Remaining);
        Assert.Equal("a few seconds", eta.RemainingText);

        // Well past it: the projection has expired and the text must say so.
        time.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal("taking longer than expected", eta.RemainingText);

        // And completion still wins over overdue.
        Assert.Equal("done", eta.Update(100));
    }

    /// <summary>
    /// Reset must clear the RATE, not merely the projection. <c>Remaining</c> is null after any reset
    /// because <c>_projectedFinish</c> is nulled, so a test that only asserts null cannot tell whether the
    /// samples were cleared — a second run would silently inherit the first run's pace.
    /// </summary>
    [Fact]
    public void Reset_ClearsTheRate_SoASecondRunDoesNotInheritTheFirstRunsPace()
    {
        var time = new TestTimeProvider();
        var eta = new EtaCalculator(time);

        // Run 1: a fast 10 %/s.
        eta.Reset();
        time.Advance(TimeSpan.FromSeconds(5));
        eta.Update(50);

        // Run 2: 5% in 1 s => 5 %/s => 95% left => exactly 19 s. Any inherited rate makes it smaller.
        eta.Reset();
        time.Advance(TimeSpan.FromSeconds(1));
        eta.Update(5);

        Assert.Equal(TimeSpan.FromSeconds(19), eta.Remaining);
    }

    /// <summary>
    /// A clock that advances on every reading, so concurrent callers see a monotonically increasing time
    /// without any test-controlled stepping. Needed because the threading test below cannot advance a
    /// frozen clock from inside the parallel body, and the real clock moves too little across a
    /// <c>Parallel.For</c> for any sample to clear the minimum interval.
    /// </summary>
    private sealed class TickingClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        // Half a second per reading: comfortably over MinSampleInterval, so samples are accepted.
        // Fully qualified from the global namespace — inside a TimeProvider subclass, `System.Threading`
        // binds to the inherited `TimeProvider.System` member first.
        public override long GetTimestamp() =>
            global::System.Threading.Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond / 2);
    }

    /// <summary>
    /// The class is fed from <c>PowerShellRunner.LineReceived</c>, which is raised on the
    /// <c>OutputDataReceived</c> AND <c>ErrorDataReceived</c> threadpool threads with nothing marshalling
    /// in between — two threads into one handler. <c>_projectedFinish</c> is a <c>TimeSpan?</c> (a 16-byte
    /// struct on x64), so an unsynchronised read can pair a live <c>HasValue</c> with a stale tick count
    /// and print an arbitrary duration.
    /// <para>Threading tests cannot prove the ABSENCE of a race, so this is a smoke check with teeth: it
    /// asserts every observed estimate is a sane duration, and that concurrent callers still produce
    /// estimates at all rather than deadlocking or leaving the state permanently unusable.</para>
    /// </summary>
    [Fact]
    public void Update_FromManyThreadsAtOnce_NeverProducesANegativeOrAbsurdEstimate()
    {
        var eta = new EtaCalculator(new TickingClock());
        eta.Reset();

        var observed = new System.Collections.Concurrent.ConcurrentBag<TimeSpan>();
        Parallel.For(0, 400, i =>
        {
            // Percent must only ever go forward — a backwards report is a no-op by design, so a shuffled
            // sequence would exercise the early return instead of the arithmetic under contention.
            eta.Update(Math.Min(99, (i / 4) + 5));
            if (eta.Remaining is { } left) observed.Add(left);
            _ = eta.RemainingText;
        });

        Assert.NotEmpty(observed);
        Assert.All(observed, t => Assert.InRange(t, TimeSpan.Zero, TimeSpan.FromDays(365)));
    }
}
