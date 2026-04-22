// SysManager · SystemSnapshotTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class SystemSnapshotTests
{
    [Fact]
    public void OsInfo_StoresFields()
    {
        var os = new OsInfo("Windows 11 Pro", "10.0.26100", "26100", TimeSpan.FromHours(3), "64-bit");
        Assert.Equal("Windows 11 Pro", os.Caption);
        Assert.Equal("10.0.26100", os.Version);
        Assert.Equal("26100", os.BuildNumber);
        Assert.Equal(3, os.Uptime.TotalHours);
        Assert.Equal("64-bit", os.Architecture);
    }

    [Fact]
    public void CpuInfo_StoresFields()
    {
        var cpu = new CpuInfo("AMD Ryzen 7 7800X3D", 8, 16, 4800, 27.5);
        Assert.Equal(8u, cpu.Cores);
        Assert.Equal(16u, cpu.LogicalProcessors);
        Assert.Equal(4800u, cpu.MaxClockMHz);
        Assert.InRange(cpu.LoadPercent, 0, 100);
    }

    [Fact]
    public void MemoryInfo_WithModules_Aggregates()
    {
        var mods = new List<MemoryModule>
        {
            new("DIMM0", "Corsair", 16, 6000, "CMH32"),
            new("DIMM2", "Corsair", 16, 6000, "CMH32"),
        };
        var mem = new MemoryInfo(32, 18, 14, 43.75, mods);
        Assert.Equal(2, mem.Modules.Count);
        Assert.Equal(32, mem.TotalGB);
        Assert.Equal(14, mem.UsedGB);
    }

    [Fact]
    public void DiskInfo_SupportsAllMediaTypes()
    {
        foreach (var media in new[] { "HDD", "SSD", "NVMe", "SCM", "Unspecified" })
        {
            var d = new DiskInfo("Disk 0", media, "NVMe", 1000, "Healthy", "OK", 42.5, 2);
            Assert.Equal(media, d.MediaType);
        }
    }

    [Fact]
    public void DiskInfo_TempAndWear_CanBeNull()
    {
        var d = new DiskInfo("Disk", "HDD", "SATA", 500, "Unknown", "OK", null, null);
        Assert.Null(d.TemperatureC);
        Assert.Null(d.WearPercent);
    }

    [Fact]
    public void SystemSnapshot_CapturesAllInfo()
    {
        var os = new OsInfo("Windows", "10", "19045", TimeSpan.FromDays(1), "64-bit");
        var cpu = new CpuInfo("Intel", 8, 16, 5000, 20);
        var mem = new MemoryInfo(32, 16, 16, 50, new List<MemoryModule>());
        var disks = new List<DiskInfo>
        {
            new("C:", "SSD", "NVMe", 1000, "Healthy", "OK", null, null),
        };
        var snap = new SystemSnapshot(os, cpu, mem, disks, DateTime.Now);
        Assert.Same(os, snap.Os);
        Assert.Same(cpu, snap.Cpu);
        Assert.Same(mem, snap.Memory);
        Assert.Single(snap.Disks);
    }

    [Fact]
    public void Records_AreEqualByValue()
    {
        var a = new OsInfo("W", "1", "1", TimeSpan.Zero, "x");
        var b = new OsInfo("W", "1", "1", TimeSpan.Zero, "x");
        Assert.Equal(a, b);
    }
}
