// SysManager · ResourceHistoryServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
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

/// <summary>
/// Disk-backed tests for <see cref="ResourceHistoryService"/>, using the injected temp directory
/// so the developer's own history in %LOCALAPPDATA% is never read or written.
/// <para>These could not exist before: the service pinned its paths to
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> in <c>static readonly</c> fields, and
/// that resolves through the Win32 known-folder API — it ignores the <c>LOCALAPPDATA</c> environment
/// variable, so not even a child process could redirect it. Every test above had to stay on the pure
/// helpers as a result, leaving the load/prune/retention paths uncovered.</para>
/// </summary>
public class ResourceHistoryServiceDiskTests : IDisposable
{
    private readonly string _dir;

    public ResourceHistoryServiceDiskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerResourceHistoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        GC.SuppressFinalize(this);
    }

    // skipHardwareInit: none of these tests read a sensor, and probing real hardware in a unit
    // test would make it environment-dependent.
    private ResourceHistoryService NewService() => new(
        new SystemInfoService(),
        new TemperatureService(new DiskHealthService(), skipHardwareInit: true),
        _dir);

    private string DataPath => Path.Combine(_dir, "resource-history.ndjson");

    private void Seed(params ResourceSample[] samples)
        => File.WriteAllLines(DataPath, samples.Select(ResourceHistoryService.Serialize));

    private static ResourceSample At(DateTime t, double cpu = 10)
        => new(t, cpu, 20, null, null, null);

    // ── The seam itself ─────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_WithNoFile_ReturnsEmpty()
    {
        using var service = NewService();
        Assert.Empty(await service.LoadAsync(TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task LoadAsync_ReadsOnlyItsOwnDirectory()
    {
        var now = DateTime.Now;
        Seed(At(now.AddMinutes(-1)));

        using var mine = NewService();
        Assert.Single(await mine.LoadAsync(TimeSpan.FromHours(1)));

        // A different directory must be independently empty — proof the path is genuinely injected
        // and not silently falling back to the shared profile location.
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        using var elsewhere = new ResourceHistoryService(
            new SystemInfoService(),
            new TemperatureService(new DiskHealthService(), skipHardwareInit: true),
            other);
        Assert.Empty(await elsewhere.LoadAsync(TimeSpan.FromDays(30)));
    }

    // ── Load: range filtering and ordering ──────────────────────────────────

    [Fact]
    public async Task LoadAsync_ExcludesSamplesOlderThanTheRange()
    {
        var now = DateTime.Now;
        Seed(
            At(now.AddDays(-3)),      // outside a 1-hour range
            At(now.AddMinutes(-30)),  // inside
            At(now.AddMinutes(-5)));  // inside

        using var service = NewService();
        var loaded = await service.LoadAsync(TimeSpan.FromHours(1));

        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public async Task LoadAsync_ReturnsOldestFirst()
    {
        // The file is append-only and time-ordered, and LoadAsync walks it backwards then reverses.
        // Chart code depends on this order, so it is asserted rather than assumed.
        var now = DateTime.Now;
        Seed(At(now.AddMinutes(-30), cpu: 1), At(now.AddMinutes(-20), cpu: 2), At(now.AddMinutes(-10), cpu: 3));

        using var service = NewService();
        var loaded = await service.LoadAsync(TimeSpan.FromHours(1));

        Assert.Equal([1d, 2d, 3d], loaded.Select(s => s.CpuPercent));
    }

    [Fact]
    public async Task LoadAsync_SkipsMalformedLinesWithoutFailing()
    {
        var now = DateTime.Now;
        File.WriteAllLines(DataPath,
        [
            "not json at all",
            ResourceHistoryService.Serialize(At(now.AddMinutes(-10))),
            "{ truncated",
        ]);

        using var service = NewService();
        var loaded = await service.LoadAsync(TimeSpan.FromHours(1));

        Assert.Single(loaded);
    }

    [Fact]
    public async Task LoadAsync_WhenTheFileCannotBeRead_ReturnsEmptyRatherThanThrowing()
    {
        // UnauthorizedAccessException is a SIBLING of IOException, not a subclass, so it escaped the
        // service's original single `catch (IOException)`. That matters because the history file is
        // read by a background sampler the user never invoked: an unhandled throw there is a crash
        // with no action that caused it. A deny-read ACL is what actually produces it — a directory
        // in the file's place does not, because File.Exists returns false and the guard short-circuits.
        Seed(At(DateTime.Now.AddMinutes(-1)));

        var identity = WindowsIdentity.GetCurrent().User;
        if (identity is null) return; // no SID to deny — nothing to assert on this host

        var info = new FileInfo(DataPath);
        var acl = info.GetAccessControl();
        var deny = new FileSystemAccessRule(identity, FileSystemRights.Read, AccessControlType.Deny);
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            using var service = NewService();
            Assert.Empty(await service.LoadAsync(TimeSpan.FromDays(7)));
        }
        finally
        {
            // Remove the deny rule, or Dispose cannot delete the temp directory.
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }

    // ── Retention persistence ───────────────────────────────────────────────

    [Fact]
    public void RetentionDays_DefaultsToSeven()
    {
        using var service = NewService();
        Assert.Equal(7, service.RetentionDays);
    }

    [Fact]
    public void RetentionDays_PersistsAcrossInstances()
    {
        using (var first = NewService())
            first.RetentionDays = 30;

        // A second instance, as after an app restart — the point of persisting at all.
        using var second = NewService();
        Assert.Equal(30, second.RetentionDays);
        Assert.True(File.Exists(Path.Combine(_dir, "resource-history-config.json")));
    }

    [Fact]
    public void RetentionDays_RejectsAValueOutsideTheOfferedOptions()
    {
        using var service = NewService();
        service.RetentionDays = 999;
        Assert.Equal(7, service.RetentionDays);
    }
}
