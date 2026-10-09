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
/// Unit tests for <see cref="DashboardViewModel"/>.
/// </summary>
/// <remarks>
/// Tests that assert what WMI returned live in IntegrationTests. The System Alerts tests at the end do wait for
/// the load, which queries WMI, and one of them runs Scan system, but they assert only on what a substitute and a
/// missing event log produce. They are here for two reasons. This is the suite that blocks a merge, and it has no
/// <c>Application</c>, so an alert's result, which is written through <c>UiThread.Post</c>, lands inline where the
/// test can read it. The integration host creates its <c>Application</c> on a thread that runs a work queue rather
/// than a dispatcher, and a result posted there would never land.
/// </remarks>
// Serialized: the confirm-gate tests swap the static DialogService.Instance,
// which is process-wide shared state.
[Collection("ProcessWideStatics")]
public class DashboardViewModelTests
{
    private static DashboardViewModel NewVm(IWingetService? winget = null,
                                            INavigationService? navigation = null,
                                            IAppBlockerService? appBlocker = null,
                                            IWindowsUpdateService? windowsUpdate = null,
                                            ISpeedTestService? speedTest = null,
                                            SpeedTestHistoryService? speedHistory = null,
                                            ITuneUpService? tuneUp = null,
                                            MemoryTestService? memTest = null,
                                            ISlowdownService? slowdown = null,
                                            IGamingProfileService? gaming = null)
    {
        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        return new DashboardViewModel(sys,
            // A substitute by default: the real one deletes this machine's temp files, and the Tune-Up also
            // empties its Recycle Bin, from any test that gets past a confirmation.
            tuneUp ?? Substitute.For<ITuneUpService>(),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true),
            winget ?? new WingetService(new PowerShellRunner()),
            // Redirected on purpose. The constructor's InitAsync reads the crash marker, and reading
            // CONSUMES it — pointed at the real profile (which is what the old optional parameter
            // defaulted to) these tests would delete a genuine crash report before the user was ever
            // told about it (#1772).
            new CrashMarkerService(Path.Combine(Path.GetTempPath(), "SysManagerTests", "dash-crash")),
            memTest ?? new MemoryTestService(),
            // A substitute by default, so a test that navigates asserts against it instead of reaching for
            // a live window. An unbound real NavigationService would also be inert, but then "did it
            // navigate?" would be unanswerable rather than merely unasked.
            navigation ?? Substitute.For<INavigationService>(),
            // A substitute by default: the real Windows Update Agent would search Microsoft's servers from any
            // test that runs the Windows Update check.
            windowsUpdate ?? Substitute.For<IWindowsUpdateService>(),
            // The real engine downloads from Cloudflare, and the real history is the user's own file. A folder
            // per construction, which nothing writes to unless a test runs the quick speed test.
            speedTest ?? Substitute.For<ISpeedTestService>(),
            speedHistory ?? new SpeedTestHistoryService(Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"))),
            // Null by default, which omits the stranded-block alert entirely — so the 58 tests written
            // before it existed keep asserting against the same five alerts they always did.
            appBlocker,
            // Null by default, which leaves "Why is it slow?" unable to run. A test that runs it passes a substitute:
            // the real check reads this machine's processes, drives and startup programs.
            slowdown,
            // Null by default, which asks nothing about a game mode session left on. A test that asks passes a
            // substitute: the real one reads this machine's record of game mode.
            gaming);
    }

    // ---------- empty states on the cards ----------

    /// <summary>
    /// A temperature read that comes back with nothing sets the flag the card explains itself with.
    /// </summary>
    /// <remarks>
    /// The harness already models the machine this is for: <c>TemperatureService(skipHardwareInit: true)</c>
    /// returns an empty list, which is exactly what a PC with no readable sensors produces — common, not
    /// exotic, since most machines expose nothing without administrator. Before the flag the card rendered
    /// as an empty box, which reads as a broken feature rather than an unavailable one.
    /// <para>This is also why the assignment sits OUTSIDE the dispatcher hop in
    /// <c>RefreshTemperaturesAsync</c>: inside it, the whole update is skipped when
    /// <c>Application.Current</c> is null — every unit test — and the state would be assertable nowhere.</para>
    /// </remarks>
    [Fact]
    public async Task RefreshTemperatures_WithNoReadableSensors_SaysSo()
    {
        var vm = NewVm();
        Assert.False(vm.TemperaturesUnavailable);   // nothing read yet

        await vm.RefreshTemperaturesCommand.ExecuteAsync(null);

        Assert.Empty(vm.Temperatures);
        Assert.True(vm.TemperaturesUnavailable);
    }

    /// <summary>
    /// Neither empty-state flag is set before anything has been read.
    /// </summary>
    /// <remarks>
    /// The half a count binding gets wrong. Both cards would otherwise announce their empty state during
    /// the first load — "no sensors could be read" while the read is in flight, "nothing needs attention"
    /// before the scan has looked at anything.
    /// <para>The health flag's behaviour is asserted in
    /// <c>SysManager.IntegrationTests.DashboardHealthFlagTests</c> rather than here, because reaching it
    /// means running the real health scan: <c>LoadHealthScoreAsync</c> is not a command, only the init path
    /// calls it, and that path also starts the polling loops and hits WMI. This class's own summary says
    /// where that belongs.</para>
    /// </remarks>
    [Fact]
    public void Constructor_EmptyStateFlags_StartFalse()
    {
        var vm = NewVm();
        Assert.False(vm.TemperaturesUnavailable);
        Assert.False(vm.HealthHasNothingToImprove);
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
    [InlineData("CheckSlowdownCommand")]
    [InlineData("CancelSlowdownCheckCommand")]
    [InlineData("DismissSlowdownReportCommand")]
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

    // ---------- a check that could not run is yellow, never green (#2479) ----------
    // Each of these three had a failure branch that wrote "… check unavailable" in green, the colour of "all good",
    // while the disk and memory checks already said they could not run, in yellow.

    [Fact]
    public void ClassifyAppUpdates_CheckFailed_IsYellowAndSaysSo()
    {
        var (title, severity) = DashboardViewModel.ClassifyAppUpdates(null);
        Assert.Equal("App updates could not be checked", title);
        Assert.Equal(AlertSeverity.Yellow, severity);
    }

    [Fact]
    public void ClassifyEventLog_CheckFailed_IsYellowAndSaysSo()
    {
        var (title, severity) = DashboardViewModel.ClassifyEventLog(null);
        Assert.Equal("Event Log could not be checked", title);
        Assert.Equal(AlertSeverity.Yellow, severity);
    }

    [Fact]
    public void ClassifyPendingReboot_CheckFailed_IsYellowAndSaysSo()
    {
        // It also used to read "Feature check unavailable", naming a check this alert does not make.
        var (title, severity) = DashboardViewModel.ClassifyPendingReboot(null);
        Assert.Equal("Pending reboot could not be checked", title);
        Assert.Equal(AlertSeverity.Yellow, severity);
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

    // ── Quick Cleanup and the Disk lock (#2473) ──

    [Fact]
    public async Task QuickCleanup_WhileAnotherDiskOperationRuns_DoesNotClean()
    {
        // The Cleanup tab's temp clean and the Quick Tune-Up take the Disk lock around this same sweep. Quick
        // Cleanup did not, so it could run alongside either, and each then reported only part of what was freed.
        using var confirm = new DialogAnswer(confirm: true);
        var tuneUp = Substitute.For<ITuneUpService>();
        var vm = NewVm(QuietWinget(), tuneUp: tuneUp);

        using (var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Quick Tune-Up"))
        {
            Assert.NotNull(held);
            await vm.QuickCleanupCommand.ExecuteAsync(null);
        }

        Assert.Equal("Failed", vm.QuickActionStatus);
        Assert.Equal("Cannot start — Quick Tune-Up is already running.", vm.QuickActionDetail);
        await tuneUp.DidNotReceiveWithAnyArgs().CleanTempFilesAsync(default);
    }

    [Fact]
    public async Task QuickCleanup_HoldsTheDiskLockWhileItCleans_AndReleasesItAfter()
    {
        using var activity = new ActivityLogScope();
        using var confirm = new DialogAnswer(confirm: true);
        string? heldBy = null;
        var tuneUp = Substitute.For<ITuneUpService>();
        tuneUp.CleanTempFilesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            heldBy = OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk);
            return Task.FromResult((BytesFreed: 300L * 1024 * 1024, FilesDeleted: 12, Errors: 0));
        });
        var vm = NewVm(QuietWinget(), tuneUp: tuneUp);

        await vm.QuickCleanupCommand.ExecuteAsync(null);

        Assert.Equal("Quick Cleanup", heldBy);
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk));
        Assert.Equal("✓ Done", vm.QuickActionStatus);
        Assert.Equal("Freed 300 MB", vm.QuickActionDetail);
    }

    [Fact]
    public async Task QuickCleanup_ThatFails_StillReleasesTheDiskLock()
    {
        // A lock left behind would refuse every later cleanup, on every tab, until the app restarted.
        using var confirm = new DialogAnswer(confirm: true);
        var tuneUp = Substitute.For<ITuneUpService>();
        tuneUp.CleanTempFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(long, int, int)>(new IOException("the temp folder went away")));
        var vm = NewVm(QuietWinget(), tuneUp: tuneUp);

        await vm.QuickCleanupCommand.ExecuteAsync(null);

        Assert.Equal("Failed", vm.QuickActionStatus);
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk));
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

    [Fact]
    public async Task QuickUpdateApps_WhenWingetFails_EndsAsFailed_NotDone()
    {
        // #2437. `winget upgrade --all` exits non-zero whenever one package fails, and the action used to return
        // normally with the failure text as its detail — so the card read "✓ Done" above "Installer failed".
        var winget = Substitute.For<IWingetService>();
        winget.UpgradeAllAsync(Arg.Any<CancellationToken>()).Returns(WingetResult.From(1));
        var vm = NewVm(winget);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.QuickUpdateAppsCommand.ExecuteAsync(null);

            Assert.Equal("Failed", vm.QuickActionStatus);
            Assert.Equal(WingetResult.From(1).FriendlyMessage, vm.QuickActionDetail);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── Update All Apps and the install lock (#2553) ──

    [Fact]
    public async Task QuickUpdateApps_WhileAnAppInstallRuns_DoesNotUpgrade()
    {
        // App Updates, Bulk Installer and Uninstaller take the install lock around their own winget runs (#2510).
        // Update All Apps runs the same upgrade and took none, so it ran beside any of them, and an MSI package in
        // either could fail with 1618, because Windows Installer runs one installation at a time.
        using var confirm = new DialogAnswer(confirm: true);
        var winget = QuietWinget();
        winget.UpgradeAllAsync(Arg.Any<CancellationToken>()).Returns(WingetResult.From(0));
        var vm = NewVm(winget);

        using (var held = OperationLockService.Instance.TryAcquire(OperationCategory.Install, "App Updates"))
        {
            Assert.NotNull(held);
            await vm.QuickUpdateAppsCommand.ExecuteAsync(null);
        }

        Assert.Equal("Failed", vm.QuickActionStatus);
        Assert.Equal("Cannot start — App Updates is already running.", vm.QuickActionDetail);
        await winget.DidNotReceiveWithAnyArgs().UpgradeAllAsync(default);
    }

    [Fact]
    public async Task QuickUpdateApps_HoldsTheInstallLockWhileItUpgrades_AndReleasesItAfter()
    {
        // The other direction: an install, upgrade or uninstall started on one of the three tabs while this runs is
        // refused, because the lock is held for the whole winget run.
        using var confirm = new DialogAnswer(confirm: true);
        string? heldBy = null;
        var winget = QuietWinget();
        winget.UpgradeAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            heldBy = OperationLockService.Instance.GetActiveOperationName(OperationCategory.Install);
            return Task.FromResult(WingetResult.From(0));
        });
        var vm = NewVm(winget);

        await vm.QuickUpdateAppsCommand.ExecuteAsync(null);

        Assert.Equal("Update All Apps", heldBy);
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.Install));
        Assert.Equal("✓ Done", vm.QuickActionStatus);
    }

    [Fact]
    public async Task QuickUpdateApps_ThatFails_StillReleasesTheInstallLock()
    {
        // A lock left behind would refuse every later install, upgrade and uninstall, on every tab, until the app
        // restarted.
        using var confirm = new DialogAnswer(confirm: true);
        var winget = QuietWinget();
        winget.UpgradeAllAsync(Arg.Any<CancellationToken>()).Returns(WingetResult.From(1));
        var vm = NewVm(winget);

        await vm.QuickUpdateAppsCommand.ExecuteAsync(null);

        Assert.Equal("Failed", vm.QuickActionStatus);
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.Install));
    }

    // ---------- Check Windows Updates: a real scan, then the way to the tab ----------
    //
    // #2437 found the action ended in "✓ Done" after half a second without contacting Windows Update, and made
    // it a plain link to the tab. It now runs the tab's own scan through the same seam, says what it found, and
    // offers the tab for choosing and installing.

    // A winget that answers at once. With the real one, every construction starts an actual `winget upgrade` for
    // the app-updates alert, and these tests are about the other button.
    private static IWingetService QuietWinget()
    {
        var winget = Substitute.For<IWingetService>();
        winget.ListUpgradableAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<AppPackage>()));
        return winget;
    }

    private static IWindowsUpdateService AgentThatFinds(int count)
    {
        var agent = Substitute.For<IWindowsUpdateService>();
        IReadOnlyList<UpdateEntry> found = Enumerable.Range(0, count)
            .Select(i => new UpdateEntry { Title = $"Update {i}", UpdateId = $"id-{i}" })
            .ToList();
        agent.ScanAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(found));
        return agent;
    }

    [Fact]
    public async Task QuickWindowsUpdate_ChecksAndSaysWhatItFound()
    {
        var agent = AgentThatFinds(3);
        var vm = NewVm(QuietWinget(), windowsUpdate: agent);

        await vm.QuickWindowsUpdateCommand.ExecuteAsync(null);

        await agent.Received(1).ScanAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Check Windows Updates", vm.QuickActionName);
        Assert.Equal("✓ Done", vm.QuickActionStatus);
        Assert.Equal("3 updates available", vm.QuickActionDetail);
    }

    [Fact]
    public async Task QuickWindowsUpdate_WhenNothingIsWaiting_SaysWindowsIsUpToDate()
    {
        var vm = NewVm(QuietWinget(), windowsUpdate: AgentThatFinds(0));

        await vm.QuickWindowsUpdateCommand.ExecuteAsync(null);

        Assert.Equal("✓ Done", vm.QuickActionStatus);
        Assert.Equal("Windows is up to date", vm.QuickActionDetail);
    }

    [Fact]
    public async Task QuickWindowsUpdate_InstallsNothing()
    {
        // The scan also lists optional drivers and feature upgrades. Choosing among those is the tab's job, so the
        // Dashboard only counts them.
        var agent = AgentThatFinds(2);
        var vm = NewVm(QuietWinget(), windowsUpdate: agent);

        await vm.QuickWindowsUpdateCommand.ExecuteAsync(null);

        await agent.DidNotReceiveWithAnyArgs().InstallAsync(default!, default);
    }

    [Fact]
    public async Task QuickWindowsUpdate_ThenOffersTheTab_AndGoesThereWhenAsked()
    {
        var navigation = Substitute.For<INavigationService>();
        var vm = NewVm(QuietWinget(), navigation: navigation, windowsUpdate: AgentThatFinds(1));

        await vm.QuickWindowsUpdateCommand.ExecuteAsync(null);

        // It does not navigate on its own: the result is on the card, and the link is the user's choice.
        navigation.DidNotReceiveWithAnyArgs().GoTo(default!, default);
        Assert.True(vm.IsQuickActionDone);
        Assert.Equal("→ Go to Windows Update for more details", vm.QuickActionNavigateLabel);

        vm.NavigateToQuickActionTabCommand.Execute(null);

        navigation.Received(1).GoTo("nav-windows-update", Arg.Any<string?>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QuickWindowsUpdate_WhenTheAgentFails_EndsAsFailed_NotUpToDate(bool refused)
    {
        var agent = Substitute.For<IWindowsUpdateService>();
        agent.ScanAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<UpdateEntry>>>(_ =>
        {
            if (refused) throw new UnauthorizedAccessException();
            throw new System.Runtime.InteropServices.COMException("search failed", unchecked((int)0x8024402C));
        });
        var vm = NewVm(QuietWinget(), windowsUpdate: agent);

        await vm.QuickWindowsUpdateCommand.ExecuteAsync(null);

        Assert.Equal("Failed", vm.QuickActionStatus);
        Assert.Equal(refused
                ? "Access denied — run SysManager as administrator."
                : "Windows Update Agent error: 0x8024402C",
            vm.QuickActionDetail);
    }

    [Theory]
    [InlineData(0, "Windows is up to date")]
    [InlineData(1, "1 update available")]
    [InlineData(12, "12 updates available")]
    public void DescribeWindowsUpdateCheck_ReadsAsASentence(int available, string expected)
        => Assert.Equal(expected, DashboardViewModel.DescribeWindowsUpdateCheck(available));

    // ---------- the quick speed test records where speed results live ----------
    //
    // It used to write its result into Recent Activity, which lists what SysManager changed on the PC, while the
    // Speed Test tab, where results are kept and compared, never saw it.

    private static readonly SpeedTestResult QuickResult =
        new("HTTP", 312.4, 41.7, 12.3, "speed.cloudflare.com", new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Local));

    private static ISpeedTestService EngineThatMeasures(SpeedTestResult result)
    {
        var engine = Substitute.For<ISpeedTestService>();
        engine.RunHttpAsync(Arg.Any<IProgress<(int Percent, string Message)>?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(result));
        return engine;
    }

    [Fact]
    public async Task QuickSpeedTest_RecordsTheResultInTheSpeedTestHistory_NotInRecentActivity()
    {
        using var activity = new ActivityLogScope();
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var history = new SpeedTestHistoryService(dir);
            var vm = NewVm(QuietWinget(), speedTest: EngineThatMeasures(QuickResult), speedHistory: history);

            await vm.QuickSpeedTestCommand.ExecuteAsync(null);

            Assert.Equal("✓ Done", vm.QuickActionStatus);
            Assert.Equal(DashboardViewModel.DescribeSpeedTest(QuickResult, saved: true), vm.QuickActionDetail);
            var saved = await history.LoadAsync();
            Assert.NotNull(saved);
            Assert.Equal(QuickResult, Assert.Single(saved));
            Assert.DoesNotContain(ActivityLogService.Instance.GetRecent(ActivityLogService.MaxEntries),
                entry => entry.Action == "Speed Test");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task QuickSpeedTest_ReachesASpeedTestTabThatIsAlreadyOpen()
    {
        // The two share one history. A tab that loaded its list before the quick test would otherwise show the
        // result only after a restart.
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var history = new SpeedTestHistoryService(dir);
            var tab = new SpeedTestViewModel(new NetworkSharedState(new PingMonitorService(), new TracerouteService(),
                new TracerouteMonitorService(), new SpeedTestService(), new NetworkRepairService(new PowerShellRunner())), history);
            await tab.InitializationComplete;
            var vm = NewVm(QuietWinget(), speedTest: EngineThatMeasures(QuickResult), speedHistory: history);

            await vm.QuickSpeedTestCommand.ExecuteAsync(null);

            Assert.Equal(QuickResult, Assert.Single(tab.HttpHistory));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task QuickSpeedTest_WhileAnotherNetworkTestRuns_DoesNotMeasure()
    {
        // Two tests at once each measure about half the line, and the quick test's reading now goes into the
        // history the Speed Test tab compares against. It takes the same lock the tab does.
        var engine = EngineThatMeasures(QuickResult);
        var vm = NewVm(QuietWinget(), speedTest: engine);

        using (var held = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "Traceroute"))
        {
            Assert.NotNull(held);
            await vm.QuickSpeedTestCommand.ExecuteAsync(null);
        }

        Assert.Equal("Failed", vm.QuickActionStatus);
        Assert.Equal("Cannot start — Traceroute is already running.", vm.QuickActionDetail);
        await engine.DidNotReceiveWithAnyArgs().RunHttpAsync(default, default);
    }

    [Theory]
    [InlineData(true, "↓ 312 Mbps · ↑ 42 Mbps · Ping 12ms")]
    [InlineData(false, "↓ 312 Mbps · ↑ 42 Mbps · Ping 12ms — not saved to the Speed Test history")]
    public void DescribeSpeedTest_SaysWhenTheResultCouldNotBeSaved(bool saved, string expected)
        => Assert.Equal(expected, DashboardViewModel.DescribeSpeedTest(QuickResult, saved));

    // A ping with no answer used to read "Ping 0ms", a perfect score (#2504).

    [Fact]
    public void DescribeSpeedTest_APingWithNoAnswer_ReadsAsADash_NotAsZero()
        => Assert.Equal("↓ 312 Mbps · ↑ 42 Mbps · Ping —",
            DashboardViewModel.DescribeSpeedTest(QuickResult with { PingMs = null }, saved: true));

    [Fact]
    public void DescribeSpeedTest_AnUploadThatWasNotMeasured_ReadsAsADash()
        => Assert.Equal("↓ 312 Mbps · ↑ — · Ping 12ms",
            DashboardViewModel.DescribeSpeedTest(QuickResult with { UploadMbps = null }, saved: true));

    [Fact]
    public async Task QuickSpeedTest_APingWithNoAnswer_IsRecordedAsNoPing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var noPing = QuickResult with { PingMs = null };
            var history = new SpeedTestHistoryService(dir);
            var vm = NewVm(QuietWinget(), speedTest: EngineThatMeasures(noPing), speedHistory: history);

            await vm.QuickSpeedTestCommand.ExecuteAsync(null);

            Assert.Equal("↓ 312 Mbps · ↑ 42 Mbps · Ping —", vm.QuickActionDetail);
            var saved = await history.LoadAsync();
            Assert.NotNull(saved);
            Assert.Null(Assert.Single(saved).PingMs);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
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
    // ---------- the two alerts that stated things the app did not know ----------

    [Fact]
    public void ClassifySmartHealth_Unavailable_SaysSoInsteadOfClaimingHealth()
    {
        // The P1, from the user's side. An unreadable Storage namespace scored 100 and landed in the green
        // branch, so "All SMART indicators healthy" appeared for a machine whose disks were never read.
        // Unknown is also not "degrading" — nothing is degrading, nothing was measured — so it gets its own
        // wording rather than borrowing the middle branch's.
        var (title, severity) = DashboardViewModel.ClassifySmartHealth(
            HealthScoreService.UnknownComponentScore, unavailable: true);

        Assert.Contains("could not be read", title);
        Assert.Equal(AlertSeverity.Yellow, severity);
        Assert.DoesNotContain("healthy", title);
        Assert.DoesNotContain("degrading", title);
    }

    [Theory]
    [InlineData(100, "All SMART indicators healthy", AlertSeverity.Green)]
    [InlineData(90, "All SMART indicators healthy", AlertSeverity.Green)]
    [InlineData(75, "Disk health degrading", AlertSeverity.Yellow)]
    [InlineData(30, "Disk health critical", AlertSeverity.Red)]
    public void ClassifySmartHealth_WithData_KeepsTheExistingThresholds(
        int diskScore, string expectedFragment, AlertSeverity expectedSeverity)
    {
        // The measured cases must be untouched by the fix — the thresholds are what the System Health tab
        // agrees with.
        var (title, severity) = DashboardViewModel.ClassifySmartHealth(diskScore, unavailable: false);

        Assert.Contains(expectedFragment, title);
        Assert.Equal(expectedSeverity, severity);
    }

    [Fact]
    public void ClassifyMemoryHealth_WheaErrors_IsRedAndCountsThem()
    {
        // The alert is titled after a 30-day hardware-error verdict and used to classify on memory USAGE, so
        // a machine with real WHEA errors at 40% usage was told "No memory errors (30 days)".
        var (title, severity) = DashboardViewModel.ClassifyMemoryHealth(
            new MemoryTestService.MemoryErrorSummary(3, 0, DateTime.Now));

        Assert.Contains("3", title);
        Assert.Equal(AlertSeverity.Red, severity);
        Assert.DoesNotContain("No memory errors", title);
    }

    [Fact]
    public void ClassifyMemoryHealth_DiagnosticRan_IsYellow()
    {
        var (title, severity) = DashboardViewModel.ClassifyMemoryHealth(
            new MemoryTestService.MemoryErrorSummary(0, 2, null));

        Assert.Contains("2", title);
        Assert.Equal(AlertSeverity.Yellow, severity);
    }

    [Fact]
    public void ClassifyMemoryHealth_NoErrors_IsGreen()
    {
        // The genuine good-news case, which is the only one allowed to say this.
        var (title, severity) = DashboardViewModel.ClassifyMemoryHealth(
            new MemoryTestService.MemoryErrorSummary(0, 0, null));

        Assert.Equal("No memory errors (30 days)", title);
        Assert.Equal(AlertSeverity.Green, severity);
    }

    [Fact]
    public void ClassifyMemoryHealth_ScanFailed_IsNotGoodNews()
    {
        // A failed scan is not the same as a clean one, and the previous code had no way to tell them apart
        // because it never ran a scan.
        var (title, severity) = DashboardViewModel.ClassifyMemoryHealth(null);

        Assert.Contains("could not be checked", title);
        Assert.Equal(AlertSeverity.Yellow, severity);
    }

    [Fact]
    public void ClassifyMemoryHealth_SingularAndPlural_BothRead()
    {
        // Shown to someone who does not know what WHEA is; "1 memory hardware errors" undermines the rest.
        var one = DashboardViewModel.ClassifyMemoryHealth(
            new MemoryTestService.MemoryErrorSummary(1, 0, null)).Title;
        var two = DashboardViewModel.ClassifyMemoryHealth(
            new MemoryTestService.MemoryErrorSummary(2, 0, null)).Title;

        Assert.Contains("1 memory hardware error ", one);
        Assert.Contains("2 memory hardware errors ", two);
    }

    // ---------- the stranded-block alert (#2357) ----------

    private static IAppBlockerService BlockerReporting(params (string Name, bool Unrecoverable)[] rows)
    {
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.GetBlockedApps().Returns([.. rows.Select(r => new BlockedApp
        {
            ExecutableName = r.Name,
            IsUnrecoverable = r.Unrecoverable,
        })]);
        return blocker;
    }

    [Fact]
    public void ClassifyStrandedBlocks_OneStranded_IsRedAndNamesIt()
    {
        // The alert exists because the damage is invisible: an IFEO block on consent.exe removes elevation
        // and nothing says so until something asks for administrator rights. The Dashboard is the landing
        // page, so it is the only place someone would see it without already suspecting it.
        var (title, severity) = DashboardViewModel.ClassifyStrandedBlocks(1, "consent.exe");

        Assert.Contains("consent.exe", title, StringComparison.Ordinal);
        Assert.Equal(AlertSeverity.Red, severity);
        Assert.Equal("nav-app-blocker", DashboardViewModel.NavTargetFor(severity, "nav-app-blocker"));
    }

    [Fact]
    public void ClassifyStrandedBlocks_NoneStranded_IsGreenAndCarriesNoButton()
    {
        // Green makes NavTargetFor return "", so the alert offers nowhere to go. A deliberate block must not
        // produce a Dashboard entry inviting the user to do something about it.
        var (title, severity) = DashboardViewModel.ClassifyStrandedBlocks(0, null);

        Assert.Equal(AlertSeverity.Green, severity);
        Assert.DoesNotContain("cannot", title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", DashboardViewModel.NavTargetFor(severity, "nav-app-blocker"));
    }

    [Fact]
    public void ClassifyStrandedBlocks_SeveralStranded_CountsThemWithoutNamingOne()
    {
        // Naming only the first of several would read as "one problem" on a machine with more, and the
        // banner on the tab is where the full list belongs.
        var (title, severity) = DashboardViewModel.ClassifyStrandedBlocks(3, "consent.exe");

        Assert.Contains("3 blocked apps", title, StringComparison.Ordinal);
        Assert.DoesNotContain("consent.exe", title, StringComparison.Ordinal);
        Assert.Equal(AlertSeverity.Red, severity);
    }

    [Fact]
    public void ClassifyStrandedBlocks_ListNotRead_IsYellowAndSaysSo()
    {
        // A block list that could not be read arrived as an empty one and read "No blocked apps need
        // attention", in green (#2503).
        var (title, severity) = DashboardViewModel.ClassifyStrandedBlocks(null, null);

        Assert.Equal("Blocked apps could not be checked", title);
        Assert.Equal(AlertSeverity.Yellow, severity);
        Assert.Equal("nav-app-blocker", DashboardViewModel.NavTargetFor(severity, "nav-app-blocker"));
    }

    [Fact]
    public async Task ABlockListThatCannotBeRead_IsYellowAndLinksToAppBlocker()
    {
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.GetBlockedApps().Returns((IReadOnlyList<BlockedApp>?)null);
        using var vm = NewVm(QuietWinget(), appBlocker: blocker);
        await LoadedWithAlertsAsync(vm);

        var alert = Assert.Single(vm.Alerts, a => a.Title == DashboardViewModel.ClassifyStrandedBlocks(null, null).Title);
        Assert.Equal(AlertSeverity.Yellow, alert.Severity);
        Assert.Equal("nav-app-blocker", alert.NavTargetId);
    }

    [Fact]
    public void NoBlockerSupplied_AddsNoSixthAlert()
    {
        // The optional parameter's other half: a caller that supplies nothing gets the five alerts it
        // always got, and no scan touches the registry.
        var vm = NewVm();

        Assert.DoesNotContain(vm.Alerts, a => a.Title.Contains("blocked", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- a game mode session a previous run left on (#2592) ----------
    //
    // Only Gaming Profile asked about it, and that tab is built when it is first opened, so after a crash game mode's
    // changes stayed applied until someone opened it. The Dashboard is built at startup and asks once it has loaded,
    // unless Gaming Profile got there first in this run.

    private static IGamingProfileService GamingLeftOn(bool notYetAsked = true)
    {
        var gaming = Substitute.For<IGamingProfileService>();
        gaming.ReadPendingRecovery().Returns(new PendingRecovery(PendingRecoveryKind.LeftOn));
        gaming.ClaimRecoveryQuestion().Returns(notYetAsked);
        gaming.RecoverPendingAsync(Arg.Any<CancellationToken>()).Returns(GamingRevertResult.Complete);
        return gaming;
    }

    [Fact]
    public async Task AtStartup_AGameModeSessionLeftOn_IsOfferedBack_AndPutBackOnYes()
    {
        var gaming = GamingLeftOn();
        using var dialog = new DialogAnswer(confirm: true);

        var vm = NewVm(gaming: gaming);
        await vm.InitializationComplete.WaitAsync(Bound);

        var asked = Assert.Single(dialog.Messages);
        Assert.StartsWith("Gaming Profile — Restore\nSysManager closed while game mode was still active last time.", asked,
            StringComparison.Ordinal);
        await gaming.Received(1).RecoverPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AtStartup_AnswerNo_PutsNothingBack()
    {
        var gaming = GamingLeftOn();
        using var dialog = new DialogAnswer(confirm: false);

        var vm = NewVm(gaming: gaming);
        await vm.InitializationComplete.WaitAsync(Bound);

        Assert.Equal(1, dialog.Calls);
        await gaming.DidNotReceive().RecoverPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AtStartup_NothingIsAsked_WhenGamingProfileAskedFirst()
    {
        var gaming = GamingLeftOn(notYetAsked: false);
        using var dialog = new DialogAnswer(confirm: true);

        var vm = NewVm(gaming: gaming);
        await vm.InitializationComplete.WaitAsync(Bound);

        Assert.Equal(0, dialog.Calls);
        await gaming.DidNotReceive().RecoverPendingAsync(Arg.Any<CancellationToken>());
    }

    // ---------- the alerts are checked again, and a check that fails says so (#2479) ----------
    //
    // The alerts were checked once, at launch. Scan system reloaded everything else and said "All systems
    // scanned", and after Update All Apps the card still read "3 app updates available" under "All apps updated".

    // Every wait below is bounded, so a machine whose WMI stalls fails these tests instead of hanging the run.
    // Generous, because the load they wait for queries WMI and reads the Event Log.
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(2);

    private static async Task LoadedWithAlertsAsync(DashboardViewModel vm)
    {
        await vm.InitializationComplete.WaitAsync(Bound);
        await vm.AlertScans.WaitAsync(Bound);
    }

    // A winget whose upgrade list the test controls, so a check made after the change can be told from one made
    // before it. The upgrade itself succeeds.
    private static IWingetService WingetListing(Func<int> upgrades)
    {
        var winget = Substitute.For<IWingetService>();
        winget.ListUpgradableAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(
            Enumerable.Range(1, upgrades()).Select(i => new AppPackage { Name = $"App {i}" }).ToList()));
        winget.UpgradeAllAsync(Arg.Any<CancellationToken>()).Returns(WingetResult.From(0));
        return winget;
    }

    [Fact]
    public async Task ScanSystem_ChecksEveryAlertAgain()
    {
        var upgrades = 3;
        using var vm = NewVm(WingetListing(() => upgrades));
        await LoadedWithAlertsAsync(vm);
        Assert.Contains(vm.Alerts, a => a.Title == DashboardViewModel.ClassifyAppUpdates(3).Title);
        var alertCount = vm.Alerts.Count;

        upgrades = 0;
        await vm.RefreshCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Contains(vm.Alerts, a => a.Title == DashboardViewModel.ClassifyAppUpdates(0).Title);
        Assert.DoesNotContain(vm.Alerts, a => a.Title == DashboardViewModel.ClassifyAppUpdates(3).Title);
        // A fresh set in place of the old one rather than a second set after it, and every check in it had
        // finished by the time Scan system said everything was scanned.
        Assert.Equal(alertCount, vm.Alerts.Count);
        Assert.All(vm.Alerts, a => Assert.Equal(AlertLoadingState.Complete, a.State));
    }

    [Fact]
    public async Task UpdateAllApps_ChecksTheAlertsAgain()
    {
        var upgrades = 3;
        using var vm = NewVm(WingetListing(() => upgrades));
        await LoadedWithAlertsAsync(vm);
        using var confirm = new DialogAnswer(confirm: true);

        upgrades = 0;
        await vm.QuickUpdateAppsCommand.ExecuteAsync(null).WaitAsync(Bound);
        await vm.AlertScans.WaitAsync(Bound);

        Assert.Equal("All apps updated", vm.QuickActionDetail);
        Assert.Contains(vm.Alerts, a => a.Title == DashboardViewModel.ClassifyAppUpdates(0).Title);
    }

    [Fact]
    public async Task AnAppUpdateCheckThatFails_IsYellowAndLinksToAppUpdates()
    {
        var winget = Substitute.For<IWingetService>();
        winget.ListUpgradableAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromException<List<AppPackage>>(new InvalidOperationException("The query failed.")));
        using var vm = NewVm(winget);
        await LoadedWithAlertsAsync(vm);

        var alert = Assert.Single(vm.Alerts, a => a.Title == DashboardViewModel.ClassifyAppUpdates(null).Title);
        Assert.Equal(AlertSeverity.Yellow, alert.Severity);
        Assert.Equal("nav-app-updates", alert.NavTargetId);
    }

    [Fact]
    public async Task AMemoryCheckThatCannotReadTheLog_SaysSoAndLinksToSystemHealth()
    {
        // Two defects in one alert. The service returned zero errors for a log it could not read, so this read
        // "No memory errors (30 days)" in green, and the alert had no Fix this link whatever it found.
        using var vm = NewVm(QuietWinget(), memTest: new MemoryTestService(MemoryTestServiceTests.NoSuchLog));
        await LoadedWithAlertsAsync(vm);

        var alert = Assert.Single(vm.Alerts, a => a.Title == DashboardViewModel.ClassifyMemoryHealth(null).Title);
        Assert.Equal(AlertSeverity.Yellow, alert.Severity);
        Assert.Equal("nav-system-health", alert.NavTargetId);
    }

    // ---------- Why is it slow? (#1529) ----------

    private static readonly SlowdownReport TwoFindings = new(
        [
            new SlowdownFinding(SlowdownKind.ProcessorBusy, "Google Chrome is using 61% of the processor", "Right now.",
                [new SlowdownAction("See what is running", "nav-processes", "Process Manager")]) { Rank = 1 },
            new SlowdownFinding(SlowdownKind.LongUptime, "Windows has not restarted in 9 days", "Not a problem by itself.", [])
                { Rank = 2 },
        ],
        [],
        new DateTime(2026, 10, 8, 15, 0, 0));

    private static ISlowdownService SlowdownThatFinds(SlowdownReport report)
    {
        var slowdown = Substitute.For<ISlowdownService>();
        slowdown.CheckAsync(Arg.Any<IProgress<SlowdownProbeStatus>?>(), Arg.Any<CancellationToken>()).Returns(report);
        return slowdown;
    }

    [Fact]
    public void WhyIsItSlow_WithNoCheckToRun_CannotBePressed()
    {
        // The designer graph and the container both supply the check. A caller that does not gets a button that cannot
        // run, rather than one that throws.
        var vm = NewVm(QuietWinget());

        Assert.False(vm.CheckSlowdownCommand.CanExecute(null));
        Assert.False(vm.IsSlowdownCheckRunning);
        Assert.False(vm.HasSlowdownReport);
        Assert.Null(vm.SlowdownReport);
    }

    [Fact]
    public async Task WhyIsItSlow_ShowsWhatTheCheckFound_AndSaysSoOnTheStatusLine()
    {
        var vm = NewVm(QuietWinget(), slowdown: SlowdownThatFinds(TwoFindings));
        Assert.True(vm.CheckSlowdownCommand.CanExecute(null));

        await vm.CheckSlowdownCommand.ExecuteAsync(null);

        Assert.Same(TwoFindings, vm.SlowdownReport);
        Assert.True(vm.HasSlowdownReport);
        Assert.False(vm.IsSlowdownCheckRunning);
        Assert.Equal("2 things are worth a look, worst first.", vm.StatusMessage);
        Assert.True(vm.CheckSlowdownCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhyIsItSlow_WhileItLooks_ListsTheFiveAsLooking_AndCannotBePressedAgain()
    {
        var slowdown = Substitute.For<ISlowdownService>();
        var vm = NewVm(QuietWinget(), slowdown: slowdown);
        (bool Running, bool CanPressAgain, bool HasReport, string[] Lines)? during = null;
        slowdown.CheckAsync(Arg.Any<IProgress<SlowdownProbeStatus>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            during = (vm.IsSlowdownCheckRunning, vm.CheckSlowdownCommand.CanExecute(null), vm.HasSlowdownReport,
                      [.. vm.SlowdownProbes.Select(p => p.Line)]);
            return Task.FromResult(TwoFindings);
        });

        await vm.CheckSlowdownCommand.ExecuteAsync(null);

        Assert.NotNull(during);
        Assert.True(during.Value.Running);
        Assert.False(during.Value.CanPressAgain);
        Assert.False(during.Value.HasReport);
        Assert.Equal(["Disk space…", "What is running…", "Startup programs…", "Memory…", "Time since the last restart…"],
                     during.Value.Lines);
    }

    /// <summary>
    /// A report marks its own row in place, and the others stay as they were. Delivered inline, so the marks have landed
    /// by the time the check returns: with no context, a report is posted to the thread pool and lands whenever it lands.
    /// </summary>
    [Fact]
    public async Task WhyIsItSlow_MarksEachOfTheFive_InItsOwnPlace_AsItIsReported()
    {
        var slowdown = Substitute.For<ISlowdownService>();
        slowdown.CheckAsync(Arg.Any<IProgress<SlowdownProbeStatus>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var progress = call.ArgAt<IProgress<SlowdownProbeStatus>?>(0)!;
            progress.Report(new SlowdownProbeStatus(SlowdownProbe.DiskSpace, Done: true, Read: true, "C: 40% full"));
            progress.Report(new SlowdownProbeStatus(SlowdownProbe.Memory, Done: true, Read: false, ""));
            return Task.FromResult(TwoFindings);
        });
        var vm = NewVm(QuietWinget(), slowdown: slowdown);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            await vm.CheckSlowdownCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal(
            ["Disk space — C: 40% full", "What is running…", "Startup programs…", "Memory — could not be read",
             "Time since the last restart…"],
            vm.SlowdownProbes.Select(p => p.Line));
    }

    /// <summary>Runs a post on the thread that made it, so a progress report has landed by the time Report returns.</summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    [Fact]
    public async Task WhyIsItSlow_HoldsTheDiskLockWhileItLooks_AndLetsGoAfter()
    {
        // As Quick Tune-Up does: a cleanup or a disk scan running in SysManager would be what the check finds.
        string? heldBy = null;
        var slowdown = Substitute.For<ISlowdownService>();
        slowdown.CheckAsync(Arg.Any<IProgress<SlowdownProbeStatus>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            heldBy = OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk);
            return Task.FromResult(TwoFindings);
        });
        var vm = NewVm(QuietWinget(), slowdown: slowdown);

        await vm.CheckSlowdownCommand.ExecuteAsync(null);

        Assert.Equal(DashboardViewModel.SlowdownCheckName, heldBy);
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk));
    }

    [Fact]
    public async Task WhyIsItSlow_WhileACleanupRuns_DoesNotLook_AndSaysWhy()
    {
        var slowdown = SlowdownThatFinds(TwoFindings);
        var vm = NewVm(QuietWinget(), slowdown: slowdown);

        using (var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup"))
        {
            Assert.NotNull(held);
            await vm.CheckSlowdownCommand.ExecuteAsync(null);
        }

        Assert.Equal("Cannot start — Deep Cleanup is already running.", vm.StatusMessage);
        Assert.False(vm.HasSlowdownReport);
        Assert.False(vm.IsSlowdownCheckRunning);
        await slowdown.DidNotReceiveWithAnyArgs().CheckAsync(default, default);
    }

    [Fact]
    public async Task WhyIsItSlow_Cancel_CancelsTheCheck_AndLetsGoOfTheLock()
    {
        var slowdown = Substitute.For<ISlowdownService>();
        var vm = NewVm(QuietWinget(), slowdown: slowdown);
        slowdown.CheckAsync(Arg.Any<IProgress<SlowdownProbeStatus>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            // Pressed while the check looks: the token the check was handed is the one Cancel cancels.
            vm.CancelSlowdownCheckCommand.Execute(null);
            call.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();
            return Task.FromResult(TwoFindings);
        });

        await vm.CheckSlowdownCommand.ExecuteAsync(null);

        Assert.Equal("Check cancelled.", vm.StatusMessage);
        Assert.False(vm.HasSlowdownReport);
        Assert.Null(vm.SlowdownReport);
        Assert.False(vm.IsSlowdownCheckRunning);
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk));
        Assert.True(vm.CheckSlowdownCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhyIsItSlow_Dismiss_PutsTheCardAway()
    {
        var vm = NewVm(QuietWinget(), slowdown: SlowdownThatFinds(TwoFindings));
        await vm.CheckSlowdownCommand.ExecuteAsync(null);

        vm.DismissSlowdownReportCommand.Execute(null);

        Assert.False(vm.HasSlowdownReport);
        Assert.Null(vm.SlowdownReport);
    }

    [Fact]
    public async Task WhyIsItSlow_AFindingsButton_OpensItsTab_AndLeavesTheCardUp()
    {
        // The buttons only open a tab, through the command the other cards' links use. The card stays, because the user
        // may want the next finding when they come back.
        var navigation = Substitute.For<INavigationService>();
        var vm = NewVm(QuietWinget(), navigation: navigation, slowdown: SlowdownThatFinds(TwoFindings));
        await vm.CheckSlowdownCommand.ExecuteAsync(null);

        vm.OpenTabCommand.Execute(vm.SlowdownReport!.Findings[0].Actions[0].NavTargetId);

        navigation.Received(1).GoTo("nav-processes", null);
        Assert.True(vm.HasSlowdownReport);
    }

    // ---------- what the Tune-Up confirmation says (#2505) ----------

    [Fact]
    public async Task RunTuneUp_Confirmation_SaysNeitherTheTempFilesNorTheBinCanBeRecovered()
    {
        // The Tune-Up always empties the bin, and its prompt used to mention it as one chore among three (#2505).
        // It then named only the bin as unrecoverable, and the temp files it deletes cannot be recovered either (#2611).
        var tuneUp = Substitute.For<ITuneUpService>();
        var vm = NewVm(tuneUp: tuneUp);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.RunTuneUpCommand.ExecuteAsync(null);

        Assert.Contains("Neither the temp files nor what is in the Recycle Bin can be recovered afterwards",
                        Assert.Single(dialog.Messages), StringComparison.Ordinal);
        await tuneUp.DidNotReceive().RunAsync(Arg.Any<bool>(), Arg.Any<IProgress<(int, string)>>(), Arg.Any<CancellationToken>());
    }

    // ---------- Escape (#2597) ----------

    [Fact]
    public async Task Escape_CancelsTheTuneUpOrTheSlowdownCheck_WhicheverRuns()
    {
        var vm = NewVm();
        await vm.InitializationComplete;
        Assert.Null(vm.EscapeCancel);

        vm.IsTuneUpRunning = true;
        Assert.Same(vm.CancelTuneUpCommand, vm.EscapeCancel);

        vm.IsTuneUpRunning = false;
        vm.IsSlowdownCheckRunning = true;
        Assert.Same(vm.CancelSlowdownCheckCommand, vm.EscapeCancel);
    }

    // ---------- the sidebar and the taskbar follow every job on the tab (#2596) ----------
    //
    // Only Refresh set IsBusy, so Quick Tune-Up, a quick action and the slowdown check ran with neither the
    // sidebar's busy mark nor the taskbar button showing anything.

    [Fact]
    public async Task QuickTuneUp_MarksTheTabBusy_AndFillsTheTaskbarButton_UntilItEnds()
    {
        var vm = NewVm();
        await vm.InitializationComplete;
        Assert.False(vm.IsBusy);

        vm.IsTuneUpRunning = true;
        vm.TuneUpProgress = 50;
        Assert.True(vm.IsBusy);
        Assert.Equal(50, vm.Progress);

        vm.IsTuneUpRunning = false;
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.Progress);
    }

    [Fact]
    public async Task AQuickAction_IsBusyWhileItRuns_AndNotOnceItIsDone()
    {
        var vm = NewVm();
        await vm.InitializationComplete;

        vm.IsQuickActionRunning = true;
        vm.QuickActionProgress = 30;
        Assert.True(vm.IsBusy);
        Assert.Equal(30, vm.Progress);

        // Its card stays on screen once it is done, until it is dismissed.
        vm.IsQuickActionDone = true;
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.Progress);
    }

    [Fact]
    public async Task TheSlowdownCheck_MarksTheTabBusy_WithNoFigureForTheTaskbar()
    {
        var vm = NewVm();
        await vm.InitializationComplete;

        vm.IsSlowdownCheckRunning = true;

        Assert.True(vm.IsBusy);
        Assert.Equal(0, vm.Progress);
    }

    [Fact]
    public async Task ARefreshThatEndsWhileQuickTuneUpRuns_LeavesTheTabBusy()
    {
        // Refresh cleared the busy flag as it ended, whatever else was still running.
        using var vm = NewVm(WingetListing(() => 0));
        await vm.InitializationComplete.WaitAsync(Bound);
        vm.IsTuneUpRunning = true;

        await vm.RefreshCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.True(vm.IsBusy);
    }
}
