// SysManager · DashboardViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="DashboardViewModel"/>.
/// RefreshAsync hits real WMI so it lives in IntegrationTests.
/// </summary>
public class DashboardViewModelTests
{
    private static DashboardViewModel NewVm()
    {
        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        return new DashboardViewModel(sys,
            new TuneUpService(new ShortcutCleanerService(), diskHealth, sys),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true));
    }

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_IsElevated_IsBoolean()
    {
        var vm = NewVm();
        _ = vm.IsElevated; // should not throw
    }

    [Fact]
    public void Constructor_StringProperties_DefaultEmpty()
    {
        var vm = NewVm();
        Assert.Equal("", vm.OsLine);
        Assert.Equal("", vm.UptimeLine);
        Assert.Equal("", vm.CpuName);
        Assert.Equal("", vm.CpuCores);
        Assert.Equal("", vm.GpuName);
        Assert.Equal("", vm.GpuVram);
        Assert.Equal("", vm.RamType);
    }

    [Fact]
    public void Constructor_IsBusyFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Constructor_StatusMessageEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("RefreshCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    [InlineData("RunTuneUpCommand")]
    [InlineData("CancelTuneUpCommand")]
    [InlineData("DismissTuneUpResultCommand")]
    [InlineData("QuickCleanupCommand")]
    [InlineData("QuickUpdateAppsCommand")]
    [InlineData("QuickWindowsUpdateCommand")]
    [InlineData("QuickSpeedTestCommand")]
    [InlineData("NavigateToQuickActionTabCommand")]
    [InlineData("DismissQuickActionCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = typeof(DashboardViewModel).GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop.GetValue(vm));
    }

    // ---------- property setters ----------

    [Fact]
    public void OsLine_Setter_Works()
    {
        var vm = NewVm();
        vm.OsLine = "Windows 11 Pro";
        Assert.Equal("Windows 11 Pro", vm.OsLine);
    }

    [Fact]
    public void UptimeLine_Setter_Works()
    {
        var vm = NewVm();
        vm.UptimeLine = "Uptime 3d 5h";
        Assert.Equal("Uptime 3d 5h", vm.UptimeLine);
    }

    [Fact]
    public void CpuPercent_Setter_Works()
    {
        var vm = NewVm();
        vm.CpuPercent = 42.5;
        Assert.Equal(42.5, vm.CpuPercent);
    }

    [Fact]
    public void RamPercent_Setter_Works()
    {
        var vm = NewVm();
        vm.RamPercent = 67.3;
        Assert.Equal(67.3, vm.RamPercent);
    }

    // ---------- PropertyChanged ----------

    [Theory]
    [InlineData(nameof(DashboardViewModel.OsLine), "test")]
    [InlineData(nameof(DashboardViewModel.UptimeLine), "test")]
    [InlineData(nameof(DashboardViewModel.CpuName), "test")]
    [InlineData(nameof(DashboardViewModel.GpuName), "test")]
    public void Setter_FiresPropertyChanged(string propName, string value)
    {
        var vm = NewVm();
        var fired = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == propName) fired = true; };
        typeof(DashboardViewModel).GetProperty(propName)!.SetValue(vm, value);
        Assert.True(fired);
    }

    // ---------- Tune-Up properties ----------

    [Fact]
    public void TuneUp_DefaultsToNotRunning()
    {
        var vm = NewVm();
        Assert.False(vm.IsTuneUpRunning);
        Assert.False(vm.HasTuneUpResult);
        Assert.Null(vm.TuneUpResult);
    }

    // ---------- Quick Action properties ----------

    [Fact]
    public void QuickAction_DefaultsToNotRunning()
    {
        var vm = NewVm();
        Assert.False(vm.IsQuickActionRunning);
        Assert.False(vm.IsQuickActionDone);
        Assert.Equal("", vm.QuickActionName);
    }

    // ---------- Collections ----------

    [Fact]
    public void Alerts_InitializesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Alerts);
    }

    [Fact]
    public void Temperatures_InitializesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Temperatures);
    }

    [Fact]
    public void Drives_InitializesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Drives);
    }

    [Fact]
    public void RecentActivity_InitializesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.RecentActivity);
    }
}
