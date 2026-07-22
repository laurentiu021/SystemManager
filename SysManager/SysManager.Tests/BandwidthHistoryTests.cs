// SysManager · BandwidthHistoryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Linq;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BandwidthHistoryService"/>'s pure NDJSON helpers — serialize/parse
/// round-trip, prune-by-retention, and downsample. These mirror the proven
/// <c>ResourceHistoryService</c> helpers and run without touching disk.
/// </summary>
public class BandwidthHistoryTests
{
    private static BandwidthSample S(DateTime t, double down, double up) => new(t, down, up);

    [Fact]
    public void SerializeParse_RoundTrips()
    {
        var sample = S(new DateTime(2026, 7, 22, 10, 30, 0), 1_250_000, 64_000);
        var line = BandwidthHistoryService.Serialize(sample);

        Assert.True(BandwidthHistoryService.TryParse(line, out var parsed));
        Assert.Equal(sample.Timestamp, parsed!.Timestamp);
        Assert.Equal(sample.DownBytesPerSec, parsed.DownBytesPerSec);
        Assert.Equal(sample.UpBytesPerSec, parsed.UpBytesPerSec);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ broken")]
    public void TryParse_RejectsBlankOrMalformed(string? line)
        => Assert.False(BandwidthHistoryService.TryParse(line, out _));

    [Fact]
    public void Prune_DropsExpiredAndMalformed_KeepsRecentSortedOldestFirst()
    {
        var now = new DateTime(2026, 7, 22, 12, 0, 0);
        var lines = new[]
        {
            BandwidthHistoryService.Serialize(S(now.AddDays(-10), 1, 1)), // expired (retention 7d)
            "garbage line",                                                // malformed
            BandwidthHistoryService.Serialize(S(now.AddHours(-1), 2, 2)), // kept
            BandwidthHistoryService.Serialize(S(now.AddDays(-2), 3, 3)),  // kept
        };

        var kept = BandwidthHistoryService.Prune(lines, now, TimeSpan.FromDays(7));

        Assert.Equal(2, kept.Count);
        // Oldest-first: the 2-day-old sample precedes the 1-hour-old one.
        Assert.True(BandwidthHistoryService.TryParse(kept[0], out var first));
        Assert.True(BandwidthHistoryService.TryParse(kept[1], out var second));
        Assert.True(first!.Timestamp < second!.Timestamp);
    }

    [Fact]
    public void Downsample_AtOrBelowCap_ReturnsUnchanged()
    {
        var now = new DateTime(2026, 7, 22, 12, 0, 0);
        var samples = Enumerable.Range(0, 10).Select(i => S(now.AddSeconds(i), i, i)).ToList();
        var result = BandwidthHistoryService.Downsample(samples, 50);
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void Downsample_ReducesToCap_AndAveragesBuckets()
    {
        var now = new DateTime(2026, 7, 22, 12, 0, 0);
        var samples = Enumerable.Range(0, 1000).Select(i => S(now.AddSeconds(i), 100, 50)).ToList();

        var result = BandwidthHistoryService.Downsample(samples, 100);

        Assert.True(result.Count <= 100);
        // Every bucket averages a constant series, so the values are preserved.
        Assert.All(result, r => Assert.Equal(100, r.DownBytesPerSec, 3));
        Assert.All(result, r => Assert.Equal(50, r.UpBytesPerSec, 3));
    }

    [Fact]
    public void Downsample_PreservesChronologicalOrder()
    {
        var now = new DateTime(2026, 7, 22, 12, 0, 0);
        var samples = Enumerable.Range(0, 500).Select(i => S(now.AddSeconds(i), i, i)).ToList();

        var result = BandwidthHistoryService.Downsample(samples, 50);

        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i].Timestamp >= result[i - 1].Timestamp);
    }
}
