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
    private static DashboardViewModel NewVm(IWingetService? winget = null,
                                            INavigationService? navigation = null,
                                            IAppBlockerService? appBlocker = null,
                                            IWindowsUpdateService? windowsUpdate = null)
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
            new CrashMarkerService(Path.Combine(Path.GetTempPath(), "SysManagerTests", "dash-crash")),
            new MemoryTestService(),
            // A substitute by default, so a test that navigates asserts against it instead of reaching for
            // a live window. An unbound real NavigationService would also be inert, but then "did it
            // navigate?" would be unanswerable rather than merely unasked.
            navigation ?? Substitute.For<INavigationService>(),
            // A substitute by default: the real agent would search Microsoft's servers from any test that runs
            // the Windows Update check.
            windowsUpdate ?? Substitute.For<IWindowsUpdateService>(),
            // Null by default, which omits the stranded-block alert entirely — so the 58 tests written
            // before it existed keep asserting against the same five alerts they always did.
            appBlocker);
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
    public void NoBlockerSupplied_AddsNoSixthAlert()
    {
        // The optional parameter's other half: a caller that supplies nothing gets the five alerts it
        // always got, and no scan touches the registry.
        var vm = NewVm();

        Assert.DoesNotContain(vm.Alerts, a => a.Title.Contains("blocked", StringComparison.OrdinalIgnoreCase));
    }
}
