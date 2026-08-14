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
}
