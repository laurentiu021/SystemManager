// SysManager · RecentChangesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.Input;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The Recent Changes tab (#1507): one look over the longest period, filtered by the chips without looking again, and
/// every line it says. The service is a substitute, so nothing here reads this PC.
/// </summary>
public sealed class RecentChangesViewModelTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 15, 0, 0);

    private readonly IRecentChangesService _service = Substitute.For<IRecentChangesService>();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();

    private static RecentChangesLook Look(IReadOnlyList<ChangeEvent>? changes = null, IReadOnlyList<ProblemEvent>? problems = null,
                                          IReadOnlyList<ChangeSource>? unreadable = null, bool firstLook = false,
                                          bool hasBaseline = true) =>
        new(changes ?? [], problems ?? [], unreadable ?? [], firstLook, hasBaseline, Now);

    private static ChangeEvent Change(ChangeKind kind, string subject, DateTime when, DateTime? since = null,
                                      string who = "Who", string detail = "") =>
        new(when, since, kind, subject, who, detail);

    private RecentChangesViewModel NewVm(RecentChangesLook look)
    {
        _service.LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(look));
        return new RecentChangesViewModel(_service, _navigation);
    }

    // ── Looking ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_LooksOnce_OverTheLongestPeriod()
    {
        using var vm = NewVm(Look());
        await vm.InitializationComplete;

        await _service.Received(1).LookAsync(RecentChangesService.LongestPeriodDays, Arg.Any<CancellationToken>());
        Assert.Null(vm.InitializationFault);
    }

    [Fact]
    public async Task TheChanges_AreShownByDay_NewestFirst()
    {
        using var vm = NewVm(Look(
        [
            Change(ChangeKind.SysManagerAction, "Turned on", Now.AddHours(-6)),
            Change(ChangeKind.UpdateFailed, "Contoso.Terminal", Now.AddDays(-1)),
            Change(ChangeKind.ProgramAppeared, "Search Pro Toolbar", Now.AddDays(-3), since: Now.AddDays(-6)),
        ]));
        await vm.InitializationComplete;

        Assert.Equal(["Today", "Yesterday", "Mon 5 Oct"], vm.Days.Select(d => d.Header));
        Assert.True(vm.HasChanges);
        Assert.False(vm.ShowEmpty);
        Assert.StartsWith("Looked at 15:00. In the last 7 days · ", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePeriodChips_ChooseWhatIsShown_WithoutLookingAgain()
    {
        using var vm = NewVm(Look([Change(ChangeKind.SysManagerAction, "Turned on", Now.AddDays(-10))]));
        await vm.InitializationComplete;

        Assert.False(vm.HasChanges);
        Assert.True(vm.ShowEmpty);
        Assert.Equal("No changes in the last 7 days", vm.EmptyTitle);

        vm.SelectedPeriod = "30 days";

        Assert.True(vm.HasChanges);
        Assert.Equal("In the last 30 days · SysManager made 1 change", vm.Summary);
        await _service.Received(1).LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheKindChips_ChooseWhatIsShown_AndTheSummaryStillCountsEveryKind()
    {
        using var vm = NewVm(Look(
        [
            Change(ChangeKind.SysManagerAction, "Turned on", Now.AddHours(-1)),
            Change(ChangeKind.WindowsUpdate, "KB5065426", Now.AddHours(-2)),
        ]));
        await vm.InitializationComplete;

        vm.SelectedCategory = "Windows";

        var row = Assert.Single(Assert.Single(vm.Days).Rows);
        Assert.Equal("Windows installed an update: KB5065426", row.Title);
        Assert.Equal("In the last 7 days · Windows installed 1 update · SysManager made 1 change", vm.Summary);

        vm.SelectedCategory = "Settings";

        Assert.True(vm.ShowEmpty);
        Assert.Equal("No changes of this kind in the last 7 days", vm.EmptyTitle);
    }

    [Fact]
    public async Task Problems_AreCountedInOneLine_ForThePeriodShown()
    {
        using var vm = NewVm(Look(problems:
        [
            new ProblemEvent(Now.AddDays(-1), StoppedResponding: false),
            new ProblemEvent(Now.AddDays(-20), StoppedResponding: true),
        ]));
        await vm.InitializationComplete;

        Assert.True(vm.HasProblems);
        Assert.Equal("Problems in the same days — 1 app crash — are in System Logs.", vm.ProblemsText);

        vm.SelectedPeriod = "30 days";

        Assert.Equal("Problems in the same days — 1 app crash, 1 app stopped responding — are in System Logs.", vm.ProblemsText);
    }

    [Fact]
    public async Task WhatCouldNotBeRead_IsSaid()
    {
        using var vm = NewVm(Look(unreadable: [ChangeSource.AppAlerts]));
        await vm.InitializationComplete;

        Assert.True(vm.HasNotes);
        Assert.Equal("New App Alerts' list could not be read, so what it noticed is missing.", vm.Notes);
    }

    [Fact]
    public async Task BeforeTheFirstLookEnds_NothingIsSaidToBeEmpty()
    {
        var pending = new TaskCompletionSource<RecentChangesLook>();
        _service.LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var vm = new RecentChangesViewModel(_service, _navigation);

        Assert.False(vm.HasLooked);
        Assert.False(vm.ShowEmpty);
        Assert.True(vm.IsBusy);
        Assert.False(vm.RefreshCommand.CanExecute(null));

        pending.SetResult(Look());
        await vm.InitializationComplete;

        Assert.True(vm.ShowEmpty);
        Assert.False(vm.IsBusy);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task ShowingTheTabAgain_LooksAgain()
    {
        // Each look is what keeps the list of installed programs, so a program that appears in between is found.
        using var vm = NewVm(Look());
        await vm.InitializationComplete;

        vm.IsActive = true;
        await vm.ShownLook;

        await _service.Received(2).LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowingTheTabWhileTheFirstLookRuns_DoesNotLookTwice()
    {
        var pending = new TaskCompletionSource<RecentChangesLook>();
        _service.LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var vm = new RecentChangesViewModel(_service, _navigation);

        vm.IsActive = true;
        pending.SetResult(Look());
        await vm.InitializationComplete;

        await _service.Received(1).LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALookAskedForWhileOneRuns_RunsOnceThatOneEnds()
    {
        var second = new TaskCompletionSource<RecentChangesLook>();
        _service.LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Look()), second.Task, Task.FromResult(Look()));
        using var vm = new RecentChangesViewModel(_service, _navigation);
        await vm.InitializationComplete;

        var refreshing = vm.RefreshCommand.ExecuteAsync(null);
        vm.IsActive = true;   // while the refresh is still looking
        second.SetResult(Look());
        await refreshing;

        await _service.Received(3).LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ALookThatFails_SaysSo()
    {
        _service.LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RecentChangesLook>(new InvalidOperationException("The service is not answering.")));
        using var vm = new RecentChangesViewModel(_service, _navigation);
        await vm.InitializationComplete;

        Assert.True(vm.ShowEmpty);
        Assert.Equal("Could not look at what changed", vm.EmptyTitle);
        Assert.Equal("SysManager could not look just now: The service is not answering.", vm.EmptyMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void ClosingTheTab_CallsOffALookInFlight()
    {
        var token = CancellationToken.None;
        var pending = new TaskCompletionSource<RecentChangesLook>();
        _service.LookAsync(Arg.Any<int>(), Arg.Do<CancellationToken>(t => token = t)).Returns(pending.Task);
        var vm = new RecentChangesViewModel(_service, _navigation);

        vm.Dispose();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task OpenTab_GoesThroughTheShellsNavigation()
    {
        using var vm = NewVm(Look());
        await vm.InitializationComplete;

        vm.OpenTabCommand.Execute("nav-uninstaller");
        vm.OpenTabCommand.Execute("");
        vm.OpenTabCommand.Execute(null);

        _navigation.Received(1).GoTo("nav-uninstaller", null);
        _navigation.ReceivedWithAnyArgs(1).GoTo(default!, default);
    }

    [Fact]
    public async Task F5_LooksAgain()
    {
        using var vm = NewVm(Look());
        await vm.InitializationComplete;

        var f5 = Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshOnF5);
        Assert.Same(vm.RefreshCommand, f5);
        await f5.ExecuteAsync(null);

        await _service.Received(2).LookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportCsv_IsOfferedOnlyWhenThereIsSomethingToExport()
    {
        using var vm = NewVm(Look([Change(ChangeKind.SysManagerAction, "Turned on", Now.AddDays(-10))]));
        await vm.InitializationComplete;

        Assert.False(vm.ExportCsvCommand.CanExecute(null));

        vm.SelectedPeriod = "30 days";

        Assert.True(vm.ExportCsvCommand.CanExecute(null));
    }

    // ── The days and their rows ─────────────────────────────────────────────

    [Fact]
    public void TwoOrMoreUpdatesOnOneDay_AreOneRowThatOpensToTheList()
    {
        var store = Change(ChangeKind.StoreAppUpdate, "Contoso.PhotoEditor", Now.AddHours(-4));
        var windows = Change(ChangeKind.WindowsUpdate, "KB5065426", Now.AddHours(-5));
        var action = Change(ChangeKind.SysManagerAction, "Turned on", Now.AddHours(-6));

        var today = Assert.Single(RecentChangesViewModel.BuildDays([store, windows, action], Now));

        Assert.Equal(["Windows installed 2 updates", "Turned on"], today.Rows.Select(r => r.Title));
        var updates = today.Rows[0];
        Assert.Equal("11:00", updates.Time);
        Assert.Equal("Windows Update", updates.Who);
        Assert.Equal("1 app update from the Microsoft Store, 1 Windows update", updates.Detail);
        Assert.Equal(["An app was updated from the Microsoft Store: Contoso.PhotoEditor", "Windows installed an update: KB5065426"],
            updates.Items);
        Assert.True(updates.HasItems);
        Assert.False(updates.HasLink);
    }

    [Fact]
    public void OneUpdateOnADay_IsItsOwnRow()
    {
        var driver = Change(ChangeKind.DriverUpdate, "Contoso Display", Now.AddDays(-1), detail: "Version 31.0.101.4502");

        var row = Assert.Single(Assert.Single(RecentChangesViewModel.BuildDays([driver], Now)).Rows);

        Assert.Equal("A driver was updated: Contoso Display", row.Title);
        Assert.Equal("Version 31.0.101.4502", row.Detail);
        Assert.False(row.HasItems);
    }

    [Fact]
    public void AFailedUpdate_IsNeverFoldedInWithTheInstalledOnes()
    {
        var days = RecentChangesViewModel.BuildDays(
        [
            Change(ChangeKind.WindowsUpdate, "KB1", Now.AddHours(-1)),
            Change(ChangeKind.WindowsUpdate, "KB2", Now.AddHours(-2)),
            Change(ChangeKind.UpdateFailed, "KB3", Now.AddHours(-3)),
        ], Now);

        Assert.Equal(["Windows installed 2 updates", "An update could not be installed: KB3"], Assert.Single(days).Rows.Select(r => r.Title));
    }

    [Fact]
    public void UpdatesAllFromTheStore_SayTheStoreInstalledThem()
    {
        var row = ChangeRow.ForUpdates(
        [
            Change(ChangeKind.StoreAppUpdate, "Contoso.A", Now),
            Change(ChangeKind.StoreAppUpdate, "Contoso.B", Now.AddMinutes(-1)),
        ]);

        Assert.Equal("Microsoft Store", row.Who);
        Assert.Equal("2 app updates from the Microsoft Store", row.Detail);
    }

    [Fact]
    public void TheListOfADaysUpdates_OpensAndCloses()
    {
        var row = ChangeRow.ForUpdates(
        [
            Change(ChangeKind.WindowsUpdate, "KB1", Now),
            Change(ChangeKind.DefenderUpdate, "Security Intelligence Update", Now.AddMinutes(-1)),
            Change(ChangeKind.DriverUpdate, "Contoso Display", Now.AddMinutes(-2)),
        ]);
        var changed = row.RecordPropertyChanges();

        Assert.Equal(("Show all 3", "Show all 3 updates"), (row.ExpandText, row.ExpandName));

        row.ToggleCommand.Execute(null);

        Assert.True(row.IsExpanded);
        Assert.Equal(("Hide the list", "Hide the list of updates"), (row.ExpandText, row.ExpandName));
        Assert.Contains(nameof(ChangeRow.ExpandText), changed);
        Assert.Contains(nameof(ChangeRow.ExpandName), changed);
        Assert.Equal("1 Windows update, 1 Defender definitions update, 1 driver", row.Detail);
    }

    [Theory]
    [InlineData("Today", 0)]
    [InlineData("Yesterday", -1)]
    [InlineData("Tue 6 Oct", -2)]
    public void ADaysHeading_SaysTodayYesterdayOrTheDate(string expected, int daysAgo)
    {
        Assert.Equal(expected, RecentChangesViewModel.DayHeader(Now.AddDays(daysAgo).Date.AddHours(1), Now));
    }

    [Fact]
    public void AChangedSettingsRow_ShowsADashForTheTime()
    {
        // When it changed is not known; only when Settings Watchdog noticed, which the line under it says.
        var row = ChangeRow.For(new ChangeEvent(Now, null, ChangeKind.SettingChanged, "Advertising ID",
            "Windows or another program", "Found On, where you saved Off. Settings Watchdog noticed at 15:00."));

        Assert.Equal("—", row.Time);
        Assert.Equal("Advertising ID changed", row.Title);
        Assert.Equal(("Review", "nav-settings-watchdog"), (row.LinkText, row.LinkNavId));
    }

    [Theory]
    [InlineData(ChangeKind.ProgramInstalled, "Open Uninstaller", "nav-uninstaller")]
    [InlineData(ChangeKind.ProgramAppeared, "Open Uninstaller", "nav-uninstaller")]
    [InlineData(ChangeKind.ProgramUpdated, "Open Uninstaller", "nav-uninstaller")]
    [InlineData(ChangeKind.ProgramDetected, "Open App Alerts", "nav-app-alerts")]
    [InlineData(ChangeKind.SettingChanged, "Review", "nav-settings-watchdog")]
    public void ARowsButton_OpensTheTabThatCanDoSomethingAboutIt(ChangeKind kind, string text, string navId)
    {
        var row = ChangeRow.For(Change(kind, "Contoso Notes", Now));

        Assert.True(row.HasLink);
        Assert.Equal((text, navId), (row.LinkText, row.LinkNavId));
        Assert.Equal($"{text}: {row.Title}", row.LinkName);
    }

    [Theory]
    [InlineData(ChangeKind.SysManagerAction)]
    [InlineData(ChangeKind.WindowsUpdate)]
    [InlineData(ChangeKind.UpdateFailed)]
    [InlineData(ChangeKind.ProgramRemoved)]
    [InlineData(ChangeKind.ProgramDisappeared)]
    public void ARowWithNothingToOpen_HasNoButton(ChangeKind kind)
    {
        var row = ChangeRow.For(Change(kind, "Contoso Notes", Now));

        Assert.False(row.HasLink);
        Assert.Equal(("", ""), (row.LinkText, row.LinkNavId));
    }

    [Theory]
    [InlineData(ChangeKind.SysManagerAction, "Turned on")]
    [InlineData(ChangeKind.WindowsUpdate, "Windows installed an update: Turned on")]
    [InlineData(ChangeKind.StoreAppUpdate, "An app was updated from the Microsoft Store: Turned on")]
    [InlineData(ChangeKind.DefenderUpdate, "Microsoft Defender's virus definitions were updated")]
    [InlineData(ChangeKind.DriverUpdate, "A driver was updated: Turned on")]
    [InlineData(ChangeKind.UpdateFailed, "An update could not be installed: Turned on")]
    [InlineData(ChangeKind.ProgramInstalled, "Installed: Turned on")]
    [InlineData(ChangeKind.ProgramRemoved, "Removed: Turned on")]
    [InlineData(ChangeKind.ProgramDisappeared, "Removed: Turned on")]
    [InlineData(ChangeKind.ProgramUpdated, "Updated: Turned on")]
    [InlineData(ChangeKind.ProgramAppeared, "New program: Turned on")]
    [InlineData(ChangeKind.ProgramDetected, "New program: Turned on")]
    [InlineData(ChangeKind.SettingChanged, "Turned on changed")]
    public void EachKindOfChange_SaysWhatHappenedInPlainWords(ChangeKind kind, string title)
    {
        Assert.Equal(title, ChangeRow.TitleOf(Change(kind, "Turned on", Now)));
    }

    [Fact]
    public void EveryKindOfChange_HasAnIconFromTheIconFont()
    {
        foreach (var kind in Enum.GetValues<ChangeKind>())
        {
            var glyph = ChangeRow.GlyphOf(kind);
            Assert.True(glyph is [>= '\uE000' and <= '\uF8FF'], $"{kind} has no icon-font glyph");
        }
        Assert.NotEqual(ChangeRow.GlyphOf(ChangeKind.ProgramInstalled), ChangeRow.GlyphOf(ChangeKind.ProgramRemoved));
    }

    // ── The lines it says ──────────────────────────────────────────────────

    [Theory]
    [InlineData("7 days", 7)]
    [InlineData("30 days", 30)]
    [InlineData("90 days", 90)]
    [InlineData("", 7)]
    [InlineData("soon", 7)]
    public void APeriodChip_StandsForItsNumberOfDays(string period, int days)
    {
        Assert.Equal(days, RecentChangesViewModel.PeriodDays(period));
    }

    [Fact]
    public void EveryPeriodChip_IsAPeriodTheTabCanShow()
    {
        Assert.All(RecentChangesViewModel.Periods,
            p => Assert.InRange(RecentChangesViewModel.PeriodDays(p), 1, RecentChangesService.LongestPeriodDays));
        Assert.Equal(RecentChangesService.LongestPeriodDays, RecentChangesViewModel.Periods.Max(RecentChangesViewModel.PeriodDays));
    }

    [Fact]
    public void EveryKindChipButAll_IsACategoryOfChange()
    {
        Assert.Equal(Enum.GetNames<ChangeCategory>(), RecentChangesViewModel.Categories.Where(c => c != "All"));
    }

    [Fact]
    public void AKindChip_ShowsOnlyItsKind()
    {
        var install = Change(ChangeKind.ProgramInstalled, "Contoso Notes", Now);

        Assert.True(RecentChangesViewModel.Matches(install, "All"));
        Assert.True(RecentChangesViewModel.Matches(install, "Programs"));
        Assert.False(RecentChangesViewModel.Matches(install, "Windows"));
    }

    [Fact]
    public void TheSummary_CountsEachKind_InThePeriod()
    {
        var summary = RecentChangesViewModel.Summarize(
        [
            Change(ChangeKind.StoreAppUpdate, "A", Now),
            Change(ChangeKind.WindowsUpdate, "B", Now),
            Change(ChangeKind.DriverUpdate, "C", Now),
            Change(ChangeKind.UpdateFailed, "D", Now),
            Change(ChangeKind.ProgramInstalled, "E", Now),
            Change(ChangeKind.ProgramDetected, "F", Now),
            Change(ChangeKind.ProgramUpdated, "G", Now),
            Change(ChangeKind.ProgramDisappeared, "H", Now),
            Change(ChangeKind.SysManagerAction, "I", Now),
            Change(ChangeKind.SettingChanged, "J", Now),
            Change(ChangeKind.SettingChanged, "K", Now),
        ], 7);

        Assert.Equal("In the last 7 days · Windows installed 3 updates · 1 update could not be installed · 2 programs were installed"
                     + " · 1 program was updated · 1 program was removed · SysManager made 1 change · 2 settings were changed",
            summary);
    }

    [Fact]
    public void TheSummary_OfNothing_IsEmpty()
    {
        Assert.Equal("", RecentChangesViewModel.Summarize([], 7));
    }

    [Fact]
    public void NoProblems_SaysNothing()
    {
        Assert.Equal("", RecentChangesViewModel.DescribeProblems([]));
    }

    [Fact]
    public void EverySourceThatCouldNotBeRead_IsOneSentence_AndAFirstLookSaysWhatComesNext()
    {
        var notes = RecentChangesViewModel.Describe(Look(
            unreadable: [ChangeSource.WindowsHistory, ChangeSource.InstalledPrograms, ChangeSource.SettingsWatchdog],
            firstLook: true));

        Assert.Equal(
            "Windows' own history could not be read just now, so the updates and installs it records are missing. Refresh to try again. "
            + "The list of installed programs could not be read just now, so some programs installed or removed may be missing. "
            + "Refresh to try again. Settings Watchdog's record could not be read, so changed settings are missing. "
            + "SysManager keeps the list of installed programs from now on. From the next look, a program that appears or goes "
            + "away in between is listed too, even one Windows keeps no record of.",
            notes);
    }

    [Fact]
    public void WithEverythingRead_ThereAreNoNotes()
    {
        Assert.Equal("", RecentChangesViewModel.Describe(Look()));
    }

    [Fact]
    public void NothingInThePeriod_SaysWhatThatMeans()
    {
        Assert.Equal(("No changes in the last 7 days",
                      "Nothing was installed or updated, SysManager changed nothing, and your saved settings still match. "
                      + "Pick a longer period above to look further back."),
            RecentChangesViewModel.DescribeEmpty(Look(), 7, "All"));
    }

    [Fact]
    public void NothingInThePeriod_ClaimsNothingItCouldNotCheck()
    {
        // With Windows' history unread, "nothing was installed" is not known; with no baseline, nor is the settings line.
        Assert.Equal("SysManager changed nothing. Pick a longer period above to look further back.",
            RecentChangesViewModel.DescribeEmpty(Look(unreadable: [ChangeSource.WindowsHistory], hasBaseline: false), 30, "All").Message);
        Assert.Equal("Nothing was installed or updated and SysManager changed nothing. Pick a longer period above to look further back.",
            RecentChangesViewModel.DescribeEmpty(Look(hasBaseline: false), 30, "All").Message);
    }

    [Fact]
    public void TheCsv_HasAHeader_SaysAWindowAsOne_AndQuotesWhatNeedsIt()
    {
        var csv = RecentChangesViewModel.ToCsv(
        [
            Change(ChangeKind.SysManagerAction, "Freed 1,2 GB", new DateTime(2026, 10, 8, 9, 12, 0), who: "You, in SysManager",
                detail: "Deep Cleanup"),
            Change(ChangeKind.ProgramAppeared, "Search Pro Toolbar", new DateTime(2026, 10, 5, 15, 20, 0),
                since: new DateTime(2026, 10, 2, 9, 0, 0), who: "An installer"),
        ]);

        Assert.Equal(
            "When,Kind,What,Who,Detail\r\n"
            + "2026-10-08 09:12,SysManager,\"Freed 1,2 GB\",\"You, in SysManager\",Deep Cleanup\r\n"
            + "between 2026-10-02 09:00 and 2026-10-05 15:20,Programs,New program: Search Pro Toolbar,An installer,\r\n",
            csv);
    }
}
