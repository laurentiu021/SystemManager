// SysManager · DashboardViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class DashboardViewModelTests
{
    // A winget that answers at once. Scan system waits for the System Alerts before it says everything was scanned
    // (#2479), and with the real one each Refresh test here would wait for an actual `winget upgrade` listing,
    // which none of them is about. The alerts themselves are covered in the unit project.
    private sealed class QuietWinget : IWingetService
    {
        public event Action<PowerShellLine>? LineReceived { add { } remove { } }

        public Task<List<AppPackage>> ListUpgradableAsync(CancellationToken ct = default) => Task.FromResult(new List<AppPackage>());

        public Task<WingetResult> UpgradeAsync(string packageId, CancellationToken ct = default) =>
            throw new NotSupportedException("No test here upgrades anything.");

        public Task<WingetResult> UpgradeAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("No test here upgrades anything.");
    }

    private static DashboardViewModel NewVm()
    {
        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        return new DashboardViewModel(
            sys,
            new TuneUpService(new ShortcutCleanerService(), diskHealth, sys),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true),
            new QuietWinget(),
            // Redirected on purpose: reading a crash marker CONSUMES it, so pointing this at the real
            // profile would delete a genuine crash report before the user saw it (#1772).
            new CrashMarkerService(Path.Combine(Path.GetTempPath(), "SysManagerTests", "dash-crash")),
            new MemoryTestService(),
            new NavigationService(),
            new WindowsUpdateService(),
            new SpeedTestService(),
            new SpeedTestHistoryService(Path.Combine(Path.GetTempPath(), "SysManagerTests", "dash-speed")));
    }

    // Built, then left to finish what its constructor starts: the start-up load and the round of alert checks it
    // begins. A test that refreshed straight away ran Refresh beside the start-up on the thread pool, where in the app
    // the two take turns on the UI thread, and both rebuilt the alert list at once until List.Insert threw (#2668).
    private static async Task<DashboardViewModel> LoadedVmAsync()
    {
        var vm = NewVm();
        await vm.InitializationComplete;
        await vm.AlertScans;
        return vm;
    }

    [Fact]
    public void Ctor_SetsElevationFlag()
    {
        var vm = NewVm();
        // Just ensures IsElevated is true/false (no throw).
        _ = vm.IsElevated;
    }

    [Fact]
    public async Task RefreshCommand_CompletesAndPopulatesFields()
    {
        var vm = await LoadedVmAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.OsLine));
        Assert.False(string.IsNullOrWhiteSpace(vm.UptimeLine));
        Assert.False(string.IsNullOrWhiteSpace(vm.CpuName));
        Assert.True(vm.RamTotalGB >= 0);
    }

    [Fact]
    public async Task RefreshCommand_ResetsBusyFlag_WhenDone()
    {
        var vm = await LoadedVmAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task RefreshCommand_SetsStatusMessage()
    {
        var vm = await LoadedVmAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public void RelaunchAsAdminCommand_Exists()
    {
        // We cannot realistically invoke RelaunchAsAdmin in a test because it
        // would try to spawn an elevated process and shut the test host down.
        // We only verify the command exists.
        var vm = NewVm();
        Assert.NotNull(vm.RelaunchAsAdminCommand);
    }
}
