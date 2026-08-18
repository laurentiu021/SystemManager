// SysManager · DashboardViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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
[Collection("ProcessWideStatics")]
public class DashboardViewModelTests
{
    private static DashboardViewModel NewVm(IWingetService? winget = null)
    {
        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        return new DashboardViewModel(sys,
            new TuneUpService(new ShortcutCleanerService(), diskHealth, sys),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true),
            winget ?? new WingetService(new PowerShellRunner()),
            // Redirected on purpose. The constructor's InitAsync reads the crash marker, and reading
            // CONSUMES it — pointed at the real profile (which is what the old optional parameter
            // defaulted to) these tests would delete a genuine crash report before the user was ever
            // told about it (#1772).
            new CrashMarkerService(Path.Combine(Path.GetTempPath(), "SysManagerTests", "dash-crash")));
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
    // The four "…_Setter_Works" round-trips that lived here were removed: each set a bare
    // [ObservableProperty] and read it straight back, which can only fail if the CommunityToolkit
    // source generator breaks. What a binding depends on — the change notification — is covered by
    // Setter_FiresPropertyChanged below, which the two percentages were added to.

    // ---------- PropertyChanged ----------

    /// <summary>
    /// Every bound property must raise <c>PropertyChanged</c>: the dashboard is written by a background
    /// poll loop, so the UI only updates on the notification. The parameter is <c>object</c> so the two
    /// percentages can join the string rows — they arrived here when their standalone round-trip tests
    /// were removed, because notification is the half that a binding actually depends on.
    /// </summary>
    [Theory]
    [InlineData(nameof(DashboardViewModel.OsLine), "test")]
    [InlineData(nameof(DashboardViewModel.UptimeLine), "test")]
    [InlineData(nameof(DashboardViewModel.CpuName), "test")]
    [InlineData(nameof(DashboardViewModel.GpuName), "test")]
    [InlineData(nameof(DashboardViewModel.CpuPercent), 42.5)]
    [InlineData(nameof(DashboardViewModel.RamPercent), 67.3)]
    public void Setter_FiresPropertyChanged(string propName, object value)
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

    [Fact]
    public async Task QuickUpdateApps_WhenConfirmed_DelegatesToInjectedWingetService()
    {
        // Regression: the Dashboard's one-click "Update All Apps" must call the INJECTED
        // WingetService (the single winget source of truth), not shell a hand-rolled winget
        // command. Before the fix it spawned a raw PowerShellRunner and never touched the
        // injected service, so this Received(1) assertion failed.
        var winget = Substitute.For<IWingetService>();
        winget.UpgradeAllAsync(Arg.Any<CancellationToken>()).Returns(WingetResult.From(0));
        var vm = NewVm(winget);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user confirms
        DialogService.Instance = dialog;
        try
        {
            await vm.QuickUpdateAppsCommand.ExecuteAsync(null);

            await winget.Received(1).UpgradeAllAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ---------- Tune-Up result card navigation ----------
    // Each finding on the card links to the tab that can act on it ("3 broken shortcuts" is only useful
    // if it can take you to the cleaner). Navigation resolves through the live MainWindow DataContext,
    // which does not exist in a unit test — so these pin what can be checked without a shell: the
    // command exists, and it degrades quietly rather than crashing when there is nothing to navigate to.
    // The actual tab switch is covered by the shell's own navigation tests.

    [Fact]
    public void OpenTabCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.OpenTabCommand);
    }

    [Theory]
    [InlineData("nav-shortcut-cleaner")]
    [InlineData("nav-processes")]
    [InlineData("nav-deep-cleanup")]
    public void OpenTabCommand_WithNoShell_DoesNotThrow(string navId)
    {
        // Application.Current.MainWindow is null under the test host, so the lookup must degrade
        // quietly. A throw here would take down the Dashboard whenever a finding button was clicked.
        var vm = NewVm();

        var ex = Record.Exception(() => vm.OpenTabCommand.Execute(navId));

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nav-does-not-exist")]
    public void OpenTabCommand_WithNothingToOpen_IsIgnored(string? navId)
    {
        // Parameter contract: null/empty are rejected before the lookup, and an unknown id finds no
        // NavItem and does nothing rather than clearing the current selection.
        var vm = NewVm();

        var ex = Record.Exception(() => vm.OpenTabCommand.Execute(navId));

        Assert.Null(ex);
    }

    [Fact]
    public void TuneUpResultCard_StartsHidden_AndDismissClearsIt()
    {
        // HasTuneUpResult drives the card's Visibility and TuneUpResult its content; both were computed
        // and never rendered, and DismissTuneUpResultCommand was unbound too.
        var vm = NewVm();
        Assert.False(vm.HasTuneUpResult);
        Assert.Null(vm.TuneUpResult);

        vm.HasTuneUpResult = true;
        vm.DismissTuneUpResultCommand.Execute(null);

        Assert.False(vm.HasTuneUpResult);
        Assert.Null(vm.TuneUpResult);
    }
}
