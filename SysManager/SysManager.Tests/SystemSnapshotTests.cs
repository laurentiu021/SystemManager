// SysManager · SystemSnapshotTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
    public void MemoryInfo_CarriesTheModulesItWasGiven()
    {
        // Renamed from MemoryInfo_WithModules_Aggregates, which asserted TotalGB == 32 and
        // UsedGB == 14 — the literals handed to the constructor a line earlier. It aggregated
        // nothing, so it would have passed with every aggregation in the app deleted. What is worth
        // pinning is that the module list survives the record, which is all this type promises.
        var mods = new List<MemoryModule>
        {
            new("DIMM0", "Corsair", 16, 6000, 6000, "CMH32"),
            new("DIMM2", "Corsair", 16, 6000, 6000, "CMH32"),
        };

        var mem = new MemoryInfo(32, 18, 14, 43.75, mods);

        Assert.Equal(["DIMM0", "DIMM2"], mem.Modules.Select(m => m.Slot));
    }

    // ---------- running below the rated speed ----------
    // WMI reports both a rated Speed and a ConfiguredClockSpeed. The gap between them is what tells
    // someone their RAM is not running at the speed they paid for — usually XMP/EXPO left off in the
    // BIOS. The value was being collected and then dropped, on a record nothing on screen reached.

    [Fact]
    public void MemoryModule_RunningBelowItsRating_IsFlagged()
    {
        var m = new MemoryModule("DIMM0", "Corsair", 16, 6000, 4800, "CMH32");

        Assert.True(m.IsUnderclocked);
    }

    [Fact]
    public void MemoryModule_RunningAtItsRating_IsNotFlagged()
    {
        var m = new MemoryModule("DIMM0", "Corsair", 16, 6000, 6000, "CMH32");

        Assert.False(m.IsUnderclocked);
    }

    [Theory]
    [InlineData(0u, 0u)]        // neither figure reported
    [InlineData(6000u, 0u)]     // Windows did not report the configured speed
    [InlineData(0u, 4800u)]     // no rating to compare against
    public void MemoryModule_WithAnUnknownSpeed_IsNotFlagged(uint rated, uint configured)
    {
        // A missing figure must not read as "underclocked" — that would warn every user whose
        // firmware does not report one of the two values.
        var m = new MemoryModule("DIMM0", "Corsair", 16, rated, configured, "CMH32");

        Assert.False(m.IsUnderclocked);
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
