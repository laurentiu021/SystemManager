// SysManager · ModelRecordTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for record-type models: PingSample, SpeedTestResult, TargetPreset,
/// and the SystemSnapshot family.
/// </summary>
public class ModelRecordTests
{
    // ---------- PingSample ----------

    [Fact]
    public void PingSample_HoldsValues()
    {
        var ts = DateTime.UtcNow;
        var s = new PingSample(ts, "8.8.8.8", 12.5, "OK");
        Assert.Equal(ts, s.Timestamp);
        Assert.Equal("8.8.8.8", s.Host);
        Assert.Equal(12.5, s.LatencyMs);
        Assert.Equal("OK", s.Status);
    }

    [Fact]
    public void PingSample_NullLatency_RepresentsTimeout()
    {
        var s = new PingSample(DateTime.UtcNow, "1.1.1.1", null, "Timeout");
        Assert.Null(s.LatencyMs);
        Assert.Equal("Timeout", s.Status);
    }

    [Fact]
    public void PingSample_RecordEquality()
    {
        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = new PingSample(ts, "8.8.8.8", 10.0, "OK");
        var b = new PingSample(ts, "8.8.8.8", 10.0, "OK");
        Assert.Equal(a, b);
    }

    // ---------- SpeedTestResult ----------

    [Fact]
    public void SpeedTestResult_HoldsValues()
    {
        var now = DateTime.Now;
        var r = new SpeedTestResult("HTTP", 100.5, 50.2, 15.3, "Cloudflare", now);
        Assert.Equal("HTTP", r.Engine);
        Assert.Equal(100.5, r.DownloadMbps);
        Assert.Equal(50.2, r.UploadMbps);
        Assert.Equal(15.3, r.PingMs);
        Assert.Equal("Cloudflare", r.Server);
        Assert.Equal(now, r.CompletedAt);
    }

    [Fact]
    public void SpeedTestResult_RecordEquality()
    {
        var ts = new DateTime(2026, 1, 1);
        var a = new SpeedTestResult("Ookla", 200, 100, 5, "Server", ts);
        var b = new SpeedTestResult("Ookla", 200, 100, 5, "Server", ts);
        Assert.Equal(a, b);
    }

    // ---------- TargetPreset ----------

    [Fact]
    public void TargetPreset_Global_HasTargets()
    {
        Assert.True(TargetPresets.Global.Targets.Count >= 3);
        Assert.Equal("Global", TargetPresets.Global.Name);
    }

    [Fact]
    public void TargetPreset_All_ContainsFivePresets()
    {
        Assert.Equal(5, TargetPresets.All.Count);
    }

    [Fact]
    public void TargetPreset_AllPresetsHaveNames()
    {
        Assert.All(TargetPresets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void TargetPreset_AllPresetsHaveDescriptions()
    {
        Assert.All(TargetPresets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void TargetPreset_AllPresetsHaveTargets()
    {
        Assert.All(TargetPresets.All, p => Assert.True(p.Targets.Count >= 3));
    }

    [Fact]
    public void TargetPreset_CS2Europe_HasValveIPs()
    {
        Assert.Contains(TargetPresets.CS2Europe.Targets, t => t.Host.StartsWith("146.66."));
    }

    // ---------- SystemSnapshot records ----------

    [Fact]
    public void OsInfo_HoldsValues()
    {
        var os = new OsInfo("Windows 11", "10.0.22631", "22631", TimeSpan.FromHours(48), "64-bit");
        Assert.Equal("Windows 11", os.Caption);
        Assert.Equal("22631", os.BuildNumber);
        Assert.Equal("64-bit", os.Architecture);
    }

    [Fact]
    public void CpuInfo_HoldsValues()
    {
        var cpu = new CpuInfo("Intel i7", 8, 16, 4500, 25.0);
        Assert.Equal(8u, cpu.Cores);
        Assert.Equal(16u, cpu.LogicalProcessors);
    }

    [Fact]
    public void MemoryInfo_HoldsValues()
    {
        var mem = new MemoryInfo(32, 16, 16, 50, new List<MemoryModule>());
        Assert.Equal(32, mem.TotalGB);
        Assert.Equal(50, mem.UsedPercent);
    }

    [Fact]
    public void DiskInfo_HoldsValues()
    {
        var d = new DiskInfo("Samsung 980", "SSD", "NVMe", 1000, "Healthy", "OK", 35.0, 5);
        Assert.Equal("Samsung 980", d.FriendlyName);
        Assert.Equal(35.0, d.TemperatureC);
        Assert.Equal(5, d.WearPercent);
    }

    [Fact]
    public void SystemSnapshot_HoldsAllComponents()
    {
        var snap = new SystemSnapshot(
            new OsInfo("Win", "10", "22631", TimeSpan.Zero, "64"),
            new CpuInfo("CPU", 4, 8, 3000, 10),
            new MemoryInfo(16, 8, 8, 50, new List<MemoryModule>()),
            new List<DiskInfo>(),
            DateTime.Now);
        Assert.NotNull(snap.Os);
        Assert.NotNull(snap.Cpu);
        Assert.NotNull(snap.Memory);
        Assert.NotNull(snap.Disks);
    }
}
