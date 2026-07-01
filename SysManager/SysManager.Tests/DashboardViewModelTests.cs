// SysManager · DashboardViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="DashboardViewModel"/>.
/// RefreshAsync hits real WMI so it lives in IntegrationTests.
/// </summary>
// Serialized: the confirm-gate tests swap the static DialogService.Instance,
// which is process-wide shared state.
[Collection("DialogService")]
public class DashboardViewModelTests
{
    private static DashboardViewModel NewVm()
    {
        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        return new DashboardViewModel(sys,
            new TuneUpService(new ShortcutCleanerService(), diskHealth, sys),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true),
            new WingetService(new PowerShellRunner()));
    }

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_IsElevated_IsBoolean()
    {
        var vm = NewVm();
        _ = vm.IsElevated; // should not throw
    }

    [Fact]
    public void Constructor_GpuProperties_DefaultEmpty()
    {
        var vm = NewVm();
        Assert.Equal("", vm.GpuName);
        Assert.Equal("", vm.GpuVram);
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

    // ---------- alert classification (regression for the dead-block-after-catch bug) ----------
    // Before the fix, a free block after each scanner's catch ran unconditionally and
    // overwrote the real result with an "unavailable / Green" alert. These assert the
    // real scan outcome is what surfaces.

    [Fact]
    public void ClassifyAppUpdates_Zero_IsGreenUpToDate()
    {
        var (title, severity) = DashboardViewModel.ClassifyAppUpdates(0);
        Assert.Equal("All apps up to date", title);
        Assert.Equal(AlertSeverity.Green, severity);
    }

    [Theory]
    [InlineData(1, "1 app update available")]
    [InlineData(5, "5 app updates available")]
    public void ClassifyAppUpdates_Positive_IsYellowWithCount(int count, string expectedTitle)
    {
        var (title, severity) = DashboardViewModel.ClassifyAppUpdates(count);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(AlertSeverity.Yellow, severity);
    }

    [Fact]
    public void ClassifyEventLog_Zero_IsGreenNoCriticalEvents()
    {
        var (title, severity) = DashboardViewModel.ClassifyEventLog(0);
        Assert.Equal("No critical events (last 7 days)", title);
        Assert.Equal(AlertSeverity.Green, severity);
    }

    [Theory]
    [InlineData(1, "1 critical event in Event Log (last 7d)")]
    [InlineData(3, "3 critical events in Event Log (last 7d)")]
    public void ClassifyEventLog_Positive_IsRedWithCount(int count, string expectedTitle)
    {
        var (title, severity) = DashboardViewModel.ClassifyEventLog(count);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(AlertSeverity.Red, severity);
    }

    [Fact]
    public void ClassifyPendingReboot_True_IsYellow()
    {
        var (title, severity) = DashboardViewModel.ClassifyPendingReboot(true);
        Assert.Equal("Pending reboot required (Windows Update)", title);
        Assert.Equal(AlertSeverity.Yellow, severity);
    }

    [Fact]
    public void ClassifyPendingReboot_False_IsGreen()
    {
        var (title, severity) = DashboardViewModel.ClassifyPendingReboot(false);
        Assert.Equal("No pending reboots", title);
        Assert.Equal(AlertSeverity.Green, severity);
    }

    // ── Confirmation-gate tests (destructive quick actions must route through Confirm) ──

    [Fact]
    public void QuickCleanup_WhenUserDeclinesConfirm_DoesNotRun()
    {
        var vm = NewVm();

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            vm.QuickCleanupCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining returns before RunQuickActionAsync, so no action ran.
            Assert.False(vm.IsQuickActionRunning);
            Assert.False(vm.IsQuickActionDone);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void QuickUpdateApps_WhenUserDeclinesConfirm_DoesNotRun()
    {
        var vm = NewVm();

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            vm.QuickUpdateAppsCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.False(vm.IsQuickActionRunning);
            Assert.False(vm.IsQuickActionDone);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }
}
