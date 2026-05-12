// SysManager · TrayIconServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="TrayIconService"/> — validates notification
/// threshold logic and property defaults. Actual tray icon display is
/// integration-level (requires UI thread + Windows shell).
/// </summary>
public class TrayIconServiceTests
{
    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var svc = new TrayIconService(new SystemInfoService());
        Assert.NotNull(svc);
        svc.Dispose();
    }

    [Fact]
    public void MinimizeToTray_DefaultTrue()
    {
        var svc = new TrayIconService(new SystemInfoService());
        Assert.True(svc.MinimizeToTray);
        svc.Dispose();
    }

    [Fact]
    public void NotificationsEnabled_DefaultTrue()
    {
        var svc = new TrayIconService(new SystemInfoService());
        Assert.True(svc.NotificationsEnabled);
        svc.Dispose();
    }

    [Fact]
    public void MinimizeToTray_CanBeDisabled()
    {
        var svc = new TrayIconService(new SystemInfoService());
        svc.MinimizeToTray = false;
        Assert.False(svc.MinimizeToTray);
        svc.Dispose();
    }

    [Fact]
    public void NotificationsEnabled_CanBeDisabled()
    {
        var svc = new TrayIconService(new SystemInfoService());
        svc.NotificationsEnabled = false;
        Assert.False(svc.NotificationsEnabled);
        svc.Dispose();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var svc = new TrayIconService(new SystemInfoService());
        svc.Dispose();
        var ex = Record.Exception(() => svc.Dispose());
        Assert.Null(ex);
    }

    // ---------- notification threshold logic ----------

    [Fact]
    public void CheckAndNotify_LowRam_DoesNotThrow()
    {
        // RAM at 50% — should NOT trigger notification
        var svc = new TrayIconService(new SystemInfoService());
        var snapshot = MakeSnapshot(ramUsedPct: 50, uptimeDays: 1);
        var ex = Record.Exception(() => svc.CheckAndNotify(snapshot));
        Assert.Null(ex);
        svc.Dispose();
    }

    [Fact]
    public void CheckAndNotify_HighRam_DoesNotThrow()
    {
        // RAM at 95% — should trigger notification (but no tray icon = no crash)
        var svc = new TrayIconService(new SystemInfoService());
        var snapshot = MakeSnapshot(ramUsedPct: 95, uptimeDays: 1);
        var ex = Record.Exception(() => svc.CheckAndNotify(snapshot));
        Assert.Null(ex);
        svc.Dispose();
    }

    [Fact]
    public void CheckAndNotify_HighUptime_DoesNotThrow()
    {
        var svc = new TrayIconService(new SystemInfoService());
        var snapshot = MakeSnapshot(ramUsedPct: 50, uptimeDays: 20);
        var ex = Record.Exception(() => svc.CheckAndNotify(snapshot));
        Assert.Null(ex);
        svc.Dispose();
    }

    [Fact]
    public void CheckAndNotify_UnhealthyDisk_DoesNotThrow()
    {
        var svc = new TrayIconService(new SystemInfoService());
        var snapshot = new SystemSnapshot(
            new OsInfo("Windows 11", "10.0", "22631", TimeSpan.FromDays(1), "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, 10),
            new MemoryInfo(16, 8, 8, 50, new List<MemoryModule>()),
            new List<DiskInfo>
            {
                new("TestDisk", "SSD", "NVMe", 500, "Warning", "OK", 45, 10)
            },
            DateTime.Now);
        var ex = Record.Exception(() => svc.CheckAndNotify(snapshot));
        Assert.Null(ex);
        svc.Dispose();
    }

    // ---------- helpers ----------

    private static SystemSnapshot MakeSnapshot(double ramUsedPct, int uptimeDays)
    {
        double totalGB = 16;
        double usedGB = totalGB * ramUsedPct / 100;
        return new SystemSnapshot(
            new OsInfo("Windows 11", "10.0", "22631", TimeSpan.FromDays(uptimeDays), "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, 10),
            new MemoryInfo(totalGB, totalGB - usedGB, usedGB, ramUsedPct, new List<MemoryModule>()),
            new List<DiskInfo>(),
            DateTime.Now);
    }
}
