// SysManager · BandwidthFormatTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the pure bandwidth math &amp; formatting: rate/size formatting (via
/// <see cref="FormatHelper"/>, the single source of truth), the counter-delta rate calculation,
/// the remote-port summary, and the threshold check. All deterministic, no OS access.
/// </summary>
public class BandwidthFormatTests
{
    // ── FormatRate (bits/sec) ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0 bps")]
    [InlineData(-5, "0 bps")]                 // negative clamps to zero
    [InlineData(100, "800 bps")]              // 100 B/s * 8 = 800 bps
    [InlineData(1000, "8 Kbps")]              // 8000 bps -> 8 Kbps (whole number at Kbps)
    [InlineData(125000, "1.0 Mbps")]          // 1,000,000 bps -> 1.0 Mbps (one decimal from Mbps)
    [InlineData(1_250_000, "10.0 Mbps")]
    [InlineData(125_000_000, "1.0 Gbps")]
    public void FormatRate_ProducesBitsPerSecond(double bytesPerSec, string expected)
        => Assert.Equal(expected, FormatHelper.FormatRate(bytesPerSec));

    [Fact]
    public void FormatRate_NaN_IsZero() => Assert.Equal("0 bps", FormatHelper.FormatRate(double.NaN));

    [Fact]
    public void BandwidthFormat_FormatRate_DelegatesToFormatHelper()
        => Assert.Equal(FormatHelper.FormatRate(1_250_000), BandwidthFormat.FormatRate(1_250_000));

    // ── RatePerSecond (counter deltas) ─────────────────────────────────────

    [Fact]
    public void RatePerSecond_NormalDelta()
        => Assert.Equal(1000.0, BandwidthFormat.RatePerSecond(5000, 6000, 1.0));

    [Fact]
    public void RatePerSecond_HalfSecondWindow_DoublesRate()
        => Assert.Equal(2000.0, BandwidthFormat.RatePerSecond(0, 1000, 0.5));

    [Fact]
    public void RatePerSecond_CounterWentBackwards_ClampsToZero()
        => Assert.Equal(0.0, BandwidthFormat.RatePerSecond(6000, 5000, 1.0)); // interface reset / wrap

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RatePerSecond_ZeroOrNegativeElapsed_IsZero(double elapsed)
        => Assert.Equal(0.0, BandwidthFormat.RatePerSecond(0, 100000, elapsed)); // no divide-by-zero / no infinity

    // ── SummarizePorts ─────────────────────────────────────────────────────

    [Fact]
    public void SummarizePorts_MostFrequentFirst()
        => Assert.Equal("443, 80", BandwidthFormat.SummarizePorts([80, 443, 443, 443, 80]));

    [Fact]
    public void SummarizePorts_IgnoresNonPositive()
        => Assert.Equal("443", BandwidthFormat.SummarizePorts([0, -1, 443]));

    [Fact]
    public void SummarizePorts_Empty_ReturnsEmpty()
        => Assert.Equal("", BandwidthFormat.SummarizePorts([0, 0]));

    [Fact]
    public void SummarizePorts_CapsWithEllipsis()
    {
        var summary = BandwidthFormat.SummarizePorts([1, 2, 3, 4, 5, 6], max: 3);
        Assert.EndsWith(", …", summary);
        Assert.Equal(3, summary.Split(',').Length - 1); // 3 ports + the ellipsis segment
    }

    // ── ExceedsThresholdMbps ───────────────────────────────────────────────

    [Fact]
    public void ExceedsThreshold_OverLimit_True()
        // 2,000,000 B/s = 16 Mbps > 10
        => Assert.True(BandwidthFormat.ExceedsThresholdMbps(2_000_000, 10));

    [Fact]
    public void ExceedsThreshold_UnderLimit_False()
        => Assert.False(BandwidthFormat.ExceedsThresholdMbps(1_000_000, 10)); // 8 Mbps < 10

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExceedsThreshold_ZeroOrNegativeThreshold_Disabled(double threshold)
        => Assert.False(BandwidthFormat.ExceedsThresholdMbps(999_999_999, threshold));
}
