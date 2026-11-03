// SysManager · DiskHealthServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DiskHealthService"/> — focuses on the pure-logic
/// verdict/mapping methods that don't require WMI.
/// </summary>
public class DiskHealthServiceTests
{
    // ---------- MapHealth ----------

    private static string InvokeMapHealth(uint v)
    {
        var m = typeof(DiskHealthService).GetMethod("MapHealth", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { v })!;
    }

    [Theory]
    [InlineData(0u, "Healthy")]
    [InlineData(1u, "Warning")]
    [InlineData(2u, "Unhealthy")]
    [InlineData(99u, "Unknown")]
    public void MapHealth_ReturnsExpected(uint input, string expected)
        => Assert.Equal(expected, InvokeMapHealth(input));

    // ---------- ApplyVerdict ----------

    private static void InvokeApplyVerdict(DiskHealthReport r)
    {
        var m = typeof(DiskHealthService).GetMethod("ApplyVerdict", BindingFlags.NonPublic | BindingFlags.Static)!;
        m.Invoke(null, new object[] { r });
    }

    [Fact]
    public void ApplyVerdict_Unhealthy_SetsRedVerdict()
    {
        var r = new DiskHealthReport { HealthStatus = "Unhealthy" };
        InvokeApplyVerdict(r);
        Assert.Contains("failing", r.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusColors.Bad, r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_Warning_SetsAmberVerdict()
    {
        var r = new DiskHealthReport { HealthStatus = "Warning" };
        InvokeApplyVerdict(r);
        Assert.Contains("warning", r.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusColors.Warning, r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_HighWear_SetsAmber()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", WearPercent = 95 };
        InvokeApplyVerdict(r);
        Assert.Contains("worn", r.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusColors.Warning, r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_HighTemp_SetsAmber()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", TemperatureC = 75 };
        InvokeApplyVerdict(r);
        Assert.Contains("hot", r.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusColors.Warning, r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_ReadErrors_SetsAmber()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", ReadErrors = 5 };
        InvokeApplyVerdict(r);
        Assert.Contains("I/O errors", r.Verdict);
        Assert.Equal(StatusColors.Warning, r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_WriteErrors_SetsAmber()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", WriteErrors = 3 };
        InvokeApplyVerdict(r);
        Assert.Contains("I/O errors", r.Verdict);
    }

    [Fact]
    public void ApplyVerdict_NothingRead_SaysWindowsCouldNotReport_InNeutral()
    {
        // Windows gave no status SysManager recognises and no counter was read, as without administrator rights: the
        // verdict said "Healthy" in green, or "Healthy — 5000 h on" from the power-on hours alone (#2753).
        var r = new DiskHealthReport { HealthStatus = "Unknown", PowerOnHours = 5000 };

        InvokeApplyVerdict(r);

        Assert.Equal(("Windows could not report this drive's health.", StatusColors.Neutral),
            (r.Verdict, r.VerdictColorHex));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(3L, 3L)]
    [InlineData(null, null)]
    public void AnErrorCount_KeepsAReportedZero(long? reported, long? expected)
    {
        // A reported 0 turned into "not reported", so every healthy drive whose counters were read showed "—" for its
        // read and write errors (#2753).
        Assert.Equal(expected, DiskHealthService.ToCount(reported));
    }

    [Fact]
    public void APowerOnTime_OfZero_IsStillNotReported()
    {
        // Drivers that do not keep the hours report 0, so a zero here stays "—", as it always has.
        Assert.Null(DiskHealthService.ToLong(0L));
    }

    [Fact]
    public void ApplyVerdict_AllGood_SetsGreen()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy" };
        InvokeApplyVerdict(r);
        Assert.Contains("Healthy", r.Verdict);
        Assert.Equal(StatusColors.Good, r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_AllGoodWithStats_IncludesDetails()
    {
        var r = new DiskHealthReport
        {
            HealthStatus = "Healthy",
            TemperatureC = 35,
            WearPercent = 10,
            PowerOnHours = 5000
        };
        InvokeApplyVerdict(r);
        Assert.Contains("35", r.Verdict);
        Assert.Contains("wear 10%", r.Verdict);
        Assert.Contains("5000", r.Verdict);
    }

    [Fact]
    public void ApplyVerdict_WearAt89_StillHealthy()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", WearPercent = 89 };
        InvokeApplyVerdict(r);
        Assert.Contains("Healthy", r.Verdict);
        Assert.Equal(StatusColors.Good, r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_TempAt69_StillHealthy()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", TemperatureC = 69 };
        InvokeApplyVerdict(r);
        Assert.Contains("Healthy", r.Verdict);
    }

    // ---------- a read that fails, and its log (#2782) ----------

    private const string Unavailable = "The RPC server is unavailable.";
    private static readonly DiskHealthRead Worked =
        new([new DiskHealthReport { FriendlyName = "Samsung SSD 990 PRO 2TB" }]);
    private static readonly DiskHealthRead Failed = new([], Unavailable);

    [Fact]
    public void ARead_IsComplete_OnlyWithNoFault()
    {
        Assert.True(Worked.Complete);
        Assert.False(Failed.Complete);
        Assert.False(new DiskHealthRead([], "").Complete);
    }

    [Fact]
    public async Task ReadAsync_SaysTheReadFailed_AndCollectAsync_StillGivesWhatWasRead()
    {
        // CollectAsync's callers judge each drive they are given and take none as not checked, so for them a read cut
        // short is still the drives it read. ReadAsync is for a caller that keeps what it reads.
        var disk = new DiskHealthReport { FriendlyName = "WDC WD40EZAZ-00SF3B0" };
        var service = new DiskHealthService(() => new DiskHealthRead([disk], "Time out"), _ => { });

        var read = await service.ReadAsync();
        var collected = await service.CollectAsync();

        Assert.False(read.Complete);
        Assert.Equal("Time out", read.Fault);
        Assert.Same(disk, Assert.Single(read.Disks));
        Assert.Same(disk, Assert.Single(collected));
    }

    [Fact]
    public async Task ARunOfFailedReads_IsLoggedOnce_AndAgainAfterAReadThatWorked()
    {
        // The Dashboard reads every 2 seconds. A fault that lasts was a log line each time, which pushes the history a
        // bug report needs out of the rotating log.
        var reads = new Queue<DiskHealthRead>([Failed, Failed, Failed, Worked, Failed]);
        List<string> logged = [];
        var service = new DiskHealthService(reads.Dequeue, logged.Add);

        for (var read = 0; read < 5; read++)
            await service.ReadAsync();

        Assert.Equal([Unavailable, Unavailable], logged);
    }

    [Fact]
    public async Task AReadThatWorked_IsNotLogged()
    {
        List<string> logged = [];
        var service = new DiskHealthService(() => Worked, logged.Add);

        await service.ReadAsync();
        await service.CollectAsync();

        Assert.Empty(logged);
    }

    // ---------- ToDouble / ToInt / ToLong helpers ----------

    private static double? InvokeToDouble(object? o)
    {
        var m = typeof(DiskHealthService).GetMethod("ToDouble", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (double?)m.Invoke(null, new[] { o });
    }

    private static int? InvokeToInt(object? o)
    {
        var m = typeof(DiskHealthService).GetMethod("ToInt", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int?)m.Invoke(null, new[] { o });
    }

    private static long? InvokeToLong(object? o)
    {
        var m = typeof(DiskHealthService).GetMethod("ToLong", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (long?)m.Invoke(null, new[] { o });
    }

    [Fact] public void ToDouble_Null_ReturnsNull() => Assert.Null(InvokeToDouble(null));
    [Fact] public void ToDouble_Zero_ReturnsNull() => Assert.Null(InvokeToDouble(0.0));
    [Fact] public void ToDouble_ValidValue_ReturnsValue() => Assert.Equal(42.5, InvokeToDouble(42.5));
    [Fact] public void ToDouble_InvalidString_ReturnsNull() => Assert.Null(InvokeToDouble("not a number"));

    [Fact] public void ToInt_Null_ReturnsNull() => Assert.Null(InvokeToInt(null));
    [Fact] public void ToInt_ValidValue_ReturnsValue() => Assert.Equal(42, InvokeToInt(42));
    [Fact] public void ToInt_InvalidString_ReturnsNull() => Assert.Null(InvokeToInt("nope"));

    [Fact] public void ToLong_Null_ReturnsNull() => Assert.Null(InvokeToLong(null));
    [Fact] public void ToLong_Zero_ReturnsNull() => Assert.Null(InvokeToLong(0L));
    [Fact] public void ToLong_ValidValue_ReturnsValue() => Assert.Equal(12345L, InvokeToLong(12345L));
    [Fact] public void ToLong_InvalidString_ReturnsNull() => Assert.Null(InvokeToLong("nope"));

    // ---------- Reliability enrichment: association navigation (no ObjectId regex) ----------
    // Regression for the Storage-Spaces SMART-drop bug: the old code validated the
    // MSFT_PhysicalDisk ObjectId against a regex (^[\w{}\-\\.:/]+$) before building a
    // WQL literal. The real Storage-provider ObjectId format embeds = and " characters,
    // so the regex rejected it and ALL reliability/SMART data was silently dropped on
    // those machines (plus a Warning logged on every ~2s temperature poll). The fix
    // navigates the association via ManagementObject.GetRelated instead — no ObjectId
    // parsing, injection-safe, format-agnostic. These tests pin that contract so the
    // brittle regex path can never be reintroduced.

    [Fact]
    public void EnrichWithReliability_TakesManagementObject_NotObjectIdString()
    {
        // The method must accept the disk ManagementObject (association source),
        // proving enrichment no longer parses/validates an ObjectId string.
        var m = typeof(DiskHealthService).GetMethod(
            "EnrichWithReliability", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        var parms = m!.GetParameters();
        Assert.Equal("System.Management.ManagementObject", parms[0].ParameterType.FullName);
        // No parameter is a string ObjectId anymore.
        Assert.DoesNotContain(parms, p => p.ParameterType == typeof(string));
    }

    [Fact]
    public void ObjectIdFormatPattern_IsRemoved()
    {
        // The brittle ObjectId-validation regex must be gone — its presence is what
        // dropped SMART data on Storage-Spaces machines.
        var regexMethod = typeof(DiskHealthService).GetMethod(
            "ObjectIdFormatPattern", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(regexMethod);
    }
}
