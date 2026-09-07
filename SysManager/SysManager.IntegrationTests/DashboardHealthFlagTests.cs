// SysManager · DashboardHealthFlagTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Landing tab's "nothing needs attention" flag, against a real health scan.
/// </summary>
/// <remarks>
/// Here rather than in <c>SysManager.Tests</c> because reaching the flag means running the scan:
/// <c>LoadHealthScoreAsync</c> is not a command, only the init path calls it, and that path also starts the
/// polling loops and queries WMI. <c>DashboardViewModelTests</c>' own summary says WMI-touching dashboard
/// work lives in this project; the unit suite keeps the "starts false" half, which needs no scan.
/// </remarks>
public class DashboardHealthFlagTests
{
    private static DashboardViewModel NewVm()
    {
        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        return new DashboardViewModel(sys,
            new TuneUpService(new ShortcutCleanerService(), diskHealth, sys),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true),
            new WingetService(new PowerShellRunner()),
            // Redirected for the same reason DashboardViewModelTests does it: reading the crash marker
            // CONSUMES it, and pointed at the real profile a test would delete a genuine crash report
            // before the user was ever told about it (#1772).
            new CrashMarkerService(Path.Combine(Path.GetTempPath(), "SysManagerTests", "dash-health-crash")),
            new MemoryTestService());
    }

    /// <summary>
    /// The flag agrees with the scan's own result on whatever machine this runs on.
    /// </summary>
    /// <remarks>
    /// An implication rather than a fixed expectation: whether this PC has recommendations is a property of
    /// the PC, and a test that demanded one answer would pass or fail on the host rather than on the code.
    /// What must hold everywhere is that the line the card shows matches what the scan found — no
    /// recommendations means it shows, any recommendation means it does not.
    /// </remarks>
    [Fact]
    public async Task NothingToImproveFlag_AgreesWithTheScanResult()
    {
        var vm = NewVm();
        await vm.InitializationComplete;

        if (!vm.HasHealthScore) return;   // the scan itself failed on this host; nothing to imply
        Assert.NotNull(vm.HealthResult);
        Assert.Equal(vm.HealthResult!.Recommendations.Count == 0, vm.HealthHasNothingToImprove);
    }
}
