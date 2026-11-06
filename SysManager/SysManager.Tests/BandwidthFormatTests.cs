// SysManager · BandwidthFormatTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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

    /// <summary>
    /// A rate that rounds to 1000 in its unit reads as the next unit, where it read "1000 Kbps" (#2862).
    /// </summary>
    [Theory]
    [InlineData(124.95, "1 Kbps")]            // 999.6 bps, read "1000 bps"
    [InlineData(124_950, "1.0 Mbps")]         // 999.6 Kbps, read "1000 Kbps"
    [InlineData(124_995_000, "1.0 Gbps")]     // 999.96 Mbps, read "1000.0 Mbps"
    public void FormatRate_ARateThatRoundsTo1000_ReadsAsTheNextUnit(double bytesPerSec, string expected)
        => Assert.Equal(expected, FormatHelper.FormatRate(bytesPerSec));

    /// <summary>
    /// Over rates of every unit, the tops and bottoms of each among them, from a fixed seed: a figure is the rate
    /// rounded as its unit shows it, under 1000 in its unit (Tbps aside), and a faster rate never reads slower.
    /// </summary>
    [Fact]
    public void FormatRate_OverRatesOfEveryUnit_ReadsTheRateInTheLargestUnitItReaches()
    {
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
        var random = new Random(2862);
        var rates = new List<double>();
        for (var power = 3; power <= 12; power += 3)
        {
            var edge = Math.Pow(10, power) / 8;   // bytes a second at the unit's first whole figure
            for (var i = 0; i < 400; i++) rates.Add(edge * (1 + (random.NextDouble() - 0.5) * 0.12));
        }
        for (var i = 0; i < 4000; i++) rates.Add(Math.Pow(10, random.NextDouble() * 14 - 1));
        rates.Sort();

        var previous = -1.0;
        foreach (var bytesPerSec in rates)
        {
            var text = FormatHelper.FormatRate(bytesPerSec);
            var parts = text.Split(' ');
            var unit = Array.IndexOf(units, parts[1]);
            var value = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var inUnit = bytesPerSec * 8 / Math.Pow(1000, unit);
            Assert.True(unit >= 0, $"{bytesPerSec}: \"{text}\" names no unit");
            Assert.True(Math.Abs(inUnit - value) <= (unit <= 1 ? 0.5 : 0.05) + 1e-9,
                $"{bytesPerSec}: \"{text}\" is not the rate");
            if (unit < units.Length - 1)
                Assert.True(value < 1000, $"{bytesPerSec}: \"{text}\" belongs to the next unit");
            var shown = value * Math.Pow(1000, unit);
            Assert.True(shown >= previous, $"{bytesPerSec}: \"{text}\" reads slower than the rate before it");
            previous = shown;
        }
    }

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
