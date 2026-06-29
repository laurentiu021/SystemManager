// SysManager · ResourceHistoryServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class ResourceHistoryServiceTests
{
    private static ResourceSample Sample(DateTime t, double cpu = 10, double ram = 20,
        double? gpu = null, double? cpuTemp = null, double? gpuTemp = null)
        => new(t, cpu, ram, gpu, cpuTemp, gpuTemp);

    // ── Serialize / parse round-trip ──────────────────────────────────────

    [Fact]
    public void SerializeThenParse_RoundTripsAllFields()
    {
        var original = Sample(new DateTime(2026, 6, 29, 12, 30, 0), cpu: 42.5, ram: 63.1,
            gpu: 88.0, cpuTemp: 55.5, gpuTemp: 70.0);

        var line = ResourceHistoryService.Serialize(original);
        Assert.True(ResourceHistoryService.TryParse(line, out var parsed));

        Assert.Equal(original.Timestamp, parsed!.Timestamp);
        Assert.Equal(42.5, parsed.CpuPercent);
        Assert.Equal(63.1, parsed.RamPercent);
        Assert.Equal(88.0, parsed.GpuPercent);
        Assert.Equal(55.5, parsed.CpuTempC);
        Assert.Equal(70.0, parsed.GpuTempC);
    }

    [Fact]
    public void Serialize_UsesShortKeys_ToBoundFileSize()
    {
        var line = ResourceHistoryService.Serialize(Sample(new DateTime(2026, 1, 1), gpu: 1, cpuTemp: 2, gpuTemp: 3));
        // Compact property names keep the on-disk NDJSON small.
        Assert.Contains("\"c\":", line);
        Assert.Contains("\"r\":", line);
        Assert.DoesNotContain("CpuPercent", line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ broken")]
    public void TryParse_RejectsBlankOrMalformed(string? line)
    {
        Assert.False(ResourceHistoryService.TryParse(line, out var s));
        Assert.Null(s);
    }

    // ── Prune ─────────────────────────────────────────────────────────────

    [Fact]
    public void Prune_DropsExpiredAndMalformed_KeepsRecent_OldestFirst()
    {
        var now = new DateTime(2026, 6, 29, 12, 0, 0);
        var lines = new[]
        {
            ResourceHistoryService.Serialize(Sample(now.AddDays(-10))), // expired (7d window)
            "garbage line",                                              // malformed
            ResourceHistoryService.Serialize(Sample(now.AddHours(-1))), // keep
            ResourceHistoryService.Serialize(Sample(now.AddDays(-3))),  // keep
        };

        var kept = ResourceHistoryService.Prune(lines, now, TimeSpan.FromDays(7));

        Assert.Equal(2, kept.Count);
        // Oldest-first: the -3d sample precedes the -1h sample.
        Assert.True(ResourceHistoryService.TryParse(kept[0], out var first));
        Assert.True(ResourceHistoryService.TryParse(kept[1], out var second));
        Assert.Equal(now.AddDays(-3), first!.Timestamp);
        Assert.Equal(now.AddHours(-1), second!.Timestamp);
    }

    [Fact]
    public void Prune_EmptyInput_ReturnsEmpty()
        => Assert.Empty(ResourceHistoryService.Prune([], DateTime.Now, TimeSpan.FromDays(7)));

    // ── Downsample ────────────────────────────────────────────────────────

    [Fact]
    public void Downsample_BelowCap_ReturnsInputUnchanged()
    {
        var now = new DateTime(2026, 6, 29);
        var input = new[] { Sample(now), Sample(now.AddMinutes(1)), Sample(now.AddMinutes(2)) };
        var result = ResourceHistoryService.Downsample(input, 400);
        Assert.Same(input, result);
    }

    [Fact]
    public void Downsample_MaxPointsZero_ClampsToOne()
    {
        var now = new DateTime(2026, 6, 29);
        var input = Enumerable.Range(0, 50).Select(i => Sample(now.AddSeconds(i), cpu: 10)).ToList();
        var result = ResourceHistoryService.Downsample(input, 0);
        Assert.Single(result); // clamped to 1 bucket, no divide-by-zero
    }

    [Fact]
    public void Downsample_MaxPointsOne_AveragesEntireSeriesIntoOneBucket()
    {
        var now = new DateTime(2026, 6, 29);
        var input = new[]
        {
            Sample(now, cpu: 20), Sample(now.AddSeconds(10), cpu: 40),
            Sample(now.AddSeconds(20), cpu: 60), Sample(now.AddSeconds(30), cpu: 80),
        };
        var result = ResourceHistoryService.Downsample(input, 1);
        Assert.Single(result);
        Assert.Equal(50, result[0].CpuPercent); // (20+40+60+80)/4
    }

    [Fact]
    public void Downsample_ExactlyMaxPoints_ReturnsUnchanged()
    {
        var now = new DateTime(2026, 6, 29);
        var input = new[] { Sample(now), Sample(now.AddSeconds(10)), Sample(now.AddSeconds(20)) };
        var result = ResourceHistoryService.Downsample(input, 3); // count == maxPoints
        Assert.Same(input, result);
    }

    [Fact]
    public void Downsample_PartialGpuNullsInBucket_AveragesOnlyPresentValues()
    {
        var now = new DateTime(2026, 6, 29);
        // One bucket: two GPU=100, two GPU=null → expect avg 100 (over present only), not 50.
        var input = new[]
        {
            Sample(now.AddSeconds(0), gpu: 100), Sample(now.AddSeconds(10), gpu: null),
            Sample(now.AddSeconds(20), gpu: 100), Sample(now.AddSeconds(30), gpu: null),
        };
        var result = ResourceHistoryService.Downsample(input, 1);
        Assert.Single(result);
        Assert.Equal(100, result[0].GpuPercent);
    }

    [Fact]
    public void Downsample_AboveCap_ReducesToAtMostMaxPoints()
    {
        var now = new DateTime(2026, 6, 29);
        var input = Enumerable.Range(0, 1000).Select(i => Sample(now.AddSeconds(i * 10), cpu: i % 100)).ToList();
        var result = ResourceHistoryService.Downsample(input, 100);
        Assert.True(result.Count <= 100);
        Assert.True(result.Count > 0);
    }

    [Fact]
    public void Downsample_AveragesUsageWithinBucket()
    {
        var now = new DateTime(2026, 6, 29);
        // Two buckets: first four samples avg to 30, last four avg to 70.
        var input = new[]
        {
            Sample(now.AddSeconds(0), cpu: 20), Sample(now.AddSeconds(10), cpu: 40),
            Sample(now.AddSeconds(20), cpu: 20), Sample(now.AddSeconds(30), cpu: 40),
            Sample(now.AddSeconds(40), cpu: 60), Sample(now.AddSeconds(50), cpu: 80),
            Sample(now.AddSeconds(60), cpu: 60), Sample(now.AddSeconds(70), cpu: 80),
        };
        var result = ResourceHistoryService.Downsample(input, 2);
        Assert.Equal(2, result.Count);
        Assert.Equal(30, result[0].CpuPercent);
        Assert.Equal(70, result[1].CpuPercent);
    }

    [Fact]
    public void Downsample_BucketWithNoGpu_LeavesGpuNull()
    {
        var now = new DateTime(2026, 6, 29);
        var input = Enumerable.Range(0, 50).Select(i => Sample(now.AddSeconds(i), cpu: 10, gpu: null)).ToList();
        var result = ResourceHistoryService.Downsample(input, 5);
        Assert.All(result, s => Assert.Null(s.GpuPercent));
    }

    // ── CSV ───────────────────────────────────────────────────────────────

    [Fact]
    public void ToCsv_HasHeader_AndRowPerSample_WithEmptyCellsForMissing()
    {
        var now = new DateTime(2026, 6, 29, 8, 0, 0);
        var samples = new[]
        {
            Sample(now, cpu: 12.3, ram: 45.6, gpu: 78.9, cpuTemp: 50, gpuTemp: 60),
            Sample(now.AddSeconds(10), cpu: 1, ram: 2, gpu: null, cpuTemp: null, gpuTemp: null),
        };

        var csv = ResourceHistoryService.ToCsv(samples);
        var lines = csv.Trim().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal("Timestamp,CPU %,RAM %,GPU %,CPU Temp °C,GPU Temp °C", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Equal("2026-06-29 08:00:00,12.3,45.6,78.9,50.0,60.0", lines[1]);
        // Missing GPU/temps render as empty trailing cells.
        Assert.Equal("2026-06-29 08:00:10,1.0,2.0,,,", lines[2]);
    }

    [Fact]
    public void ToCsv_UsesInvariantDecimalSeparator()
    {
        var csv = ResourceHistoryService.ToCsv([Sample(new DateTime(2026, 6, 29), cpu: 12.5)]);
        Assert.Contains("12.5", csv);   // dot, never comma — comma is the CSV delimiter
    }

    // ── Config contract ─────────────────────────────────────────────────────

    [Fact]
    public void RetentionOptions_AreSevenFourteenThirty()
        => Assert.Equal([7, 14, 30], ResourceHistoryService.RetentionOptions);
}
