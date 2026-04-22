using System.Reflection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DiskHealthService"/> — focuses on the pure-logic
/// verdict/mapping methods that don't require WMI.
/// </summary>
public class DiskHealthServiceTests
{
    // ---------- MapMedia ----------

    private static string InvokeMapMedia(uint v)
    {
        var m = typeof(DiskHealthService).GetMethod("MapMedia", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { v })!;
    }

    [Theory]
    [InlineData(3u, "HDD")]
    [InlineData(4u, "SSD")]
    [InlineData(5u, "SCM")]
    [InlineData(0u, "Unspecified")]
    [InlineData(99u, "Unspecified")]
    public void MapMedia_ReturnsExpected(uint input, string expected)
        => Assert.Equal(expected, InvokeMapMedia(input));

    // ---------- MapBus ----------

    private static string InvokeMapBus(uint v)
    {
        var m = typeof(DiskHealthService).GetMethod("MapBus", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { v })!;
    }

    [Theory]
    [InlineData(1u, "SCSI")]
    [InlineData(3u, "ATA")]
    [InlineData(6u, "Fibre")]
    [InlineData(7u, "USB")]
    [InlineData(8u, "RAID")]
    [InlineData(9u, "iSCSI")]
    [InlineData(10u, "SAS")]
    [InlineData(11u, "SATA")]
    [InlineData(17u, "NVMe")]
    [InlineData(0u, "Other")]
    [InlineData(99u, "Other")]
    public void MapBus_ReturnsExpected(uint input, string expected)
        => Assert.Equal(expected, InvokeMapBus(input));

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
        Assert.Equal("#EF4444", r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_Warning_SetsAmberVerdict()
    {
        var r = new DiskHealthReport { HealthStatus = "Warning" };
        InvokeApplyVerdict(r);
        Assert.Contains("warning", r.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#F59E0B", r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_HighWear_SetsAmber()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", WearPercent = 95 };
        InvokeApplyVerdict(r);
        Assert.Contains("worn", r.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#F59E0B", r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_HighTemp_SetsAmber()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", TemperatureC = 75 };
        InvokeApplyVerdict(r);
        Assert.Contains("hot", r.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#F59E0B", r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_ReadErrors_SetsAmber()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", ReadErrors = 5 };
        InvokeApplyVerdict(r);
        Assert.Contains("I/O errors", r.Verdict);
        Assert.Equal("#F59E0B", r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_WriteErrors_SetsAmber()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", WriteErrors = 3 };
        InvokeApplyVerdict(r);
        Assert.Contains("I/O errors", r.Verdict);
    }

    [Fact]
    public void ApplyVerdict_AllGood_SetsGreen()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy" };
        InvokeApplyVerdict(r);
        Assert.Contains("Healthy", r.Verdict);
        Assert.Equal("#22C55E", r.VerdictColorHex);
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
        Assert.Equal("#22C55E", r.VerdictColorHex);
    }

    [Fact]
    public void ApplyVerdict_TempAt69_StillHealthy()
    {
        var r = new DiskHealthReport { HealthStatus = "Healthy", TemperatureC = 69 };
        InvokeApplyVerdict(r);
        Assert.Contains("Healthy", r.Verdict);
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
}
