// SysManager · RecentChangesServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Recent Changes puts every source in one list, each change once and in plain words (#1507). Every source is a
/// substitute and the clock is fixed, so nothing here reads this PC.
/// </summary>
public sealed class RecentChangesServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 15, 0, 0);

    private readonly IReliabilityHistory _reliability = Substitute.For<IReliabilityHistory>();
    private readonly IInstalledProgramsHistory _programs = Substitute.For<IInstalledProgramsHistory>();
    private readonly IAppAlertHistory _alerts = Substitute.For<IAppAlertHistory>();
    private readonly ISettingsWatchdogService _watchdog = Substitute.For<ISettingsWatchdogService>();
    private IReadOnlyList<ActivityEntry> _activity = [];

    public RecentChangesServiceTests()
    {
        Windows();
        _programs.Observe(Arg.Any<DateTime>()).Returns(new ProgramsLook([], FirstLook: false, Readable: true));
        _alerts.Load().Returns(new AppAlertsRead([], Readable: true));
        _watchdog.Catalog.Returns([Ads]);
        _watchdog.LoadBaseline().Returns((BaselineSnapshot?)null);
    }

    /// <summary>A clock that always says <see cref="Now"/>, in a time zone with no offset, so local time is that too.</summary>
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly WatchedSetting Ads = new(
        "ads", "Advertising ID", "desc", "Privacy", @"HKCU\SOFTWARE\Test\ads", "Enabled",
        new Dictionary<int, string> { [0] = "Off", [1] = "On" });

    private RecentChangesService NewService() => new(_reliability, _programs, _alerts, _watchdog, () => _activity, new FixedClock());

    private void Windows(params ReliabilityRecord[] records) =>
        _reliability.ReadAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new ReliabilityRead(records, Readable: true));

    private static ChangeEvent Change(ChangeKind kind, string subject, DateTime when, DateTime? since = null) =>
        new(when, since, kind, subject, "Who", "Detail");

    // ── One look ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ALook_AsksWindowsOnlyForThePeriod()
    {
        await NewService().LookAsync(7);

        await _reliability.Received(1).ReadAsync(Now.AddDays(-7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALook_KeepsOnlyWhatChangedInThePeriod()
    {
        _activity =
        [
            new ActivityEntry("Deep Cleanup", "Freed 1.2 GB", Now.AddDays(-1)),
            new ActivityEntry("Quick Cleanup", "Freed 300 MB", Now.AddDays(-8)),
        ];

        var look = await NewService().LookAsync(7);

        Assert.Equal("Freed 1.2 GB", Assert.Single(look.Changes).Subject);
        Assert.Equal(Now, look.LookedAt);
    }

    [Fact]
    public async Task ALook_ListsEverySourceTogether_NewestFirst()
    {
        _activity = [new ActivityEntry("Performance Mode", "Turned on", Now.AddHours(-1))];
        Windows(new ReliabilityRecord(Now.AddHours(-3), "Microsoft-Windows-WindowsUpdateClient", 19,
            "9NRZT3Q9R3DL-Contoso.PhotoEditor", "Installation Successful"));
        _alerts.Load().Returns(new AppAlertsRead(
            [new AppInstallEntry { Name = "Search Pro Toolbar", DetectedAt = Now.AddHours(-2), Source = "Registry" }], Readable: true));
        _programs.Observe(Now).Returns(new ProgramsLook(
            [new ProgramChange("Contoso Paint", "Contoso", ChangeKind.ProgramDisappeared, Now.AddDays(-2), Now.AddHours(-4))],
            FirstLook: false, Readable: true));

        var look = await NewService().LookAsync(7);

        Assert.Equal(
            [ChangeKind.SysManagerAction, ChangeKind.ProgramDetected, ChangeKind.StoreAppUpdate, ChangeKind.ProgramDisappeared],
            look.Changes.Select(c => c.Kind));
        Assert.Empty(look.Unreadable);
    }

    [Fact]
    public async Task ALook_LooksAtTheProgramsAtThatMoment()
    {
        await NewService().LookAsync(7);

        _programs.Received(1).Observe(Now);
    }

    [Fact]
    public async Task ALookOfLessThanADay_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewService().LookAsync(0));
    }

    [Fact]
    public async Task EverySourceThatCouldNotBeRead_IsNamed()
    {
        _reliability.ReadAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(ReliabilityRead.Unreadable);
        _programs.Observe(Arg.Any<DateTime>()).Returns(ProgramsLook.Unreadable);
        _alerts.Load().Returns(AppAlertsRead.Unreadable);
        _watchdog.LoadBaseline().Returns(new BaselineSnapshot(Now.AddDays(-30), []));
        _watchdog.RecordDrift(Arg.Any<IReadOnlyList<SettingDrift>>(), Arg.Any<DateTime>()).Returns(DriftSightings.Unreadable);

        var look = await NewService().LookAsync(7);

        Assert.Equal(
            [ChangeSource.WindowsHistory, ChangeSource.InstalledPrograms, ChangeSource.AppAlerts, ChangeSource.SettingsWatchdog],
            look.Unreadable);
    }

    [Fact]
    public async Task WithNoBaseline_NothingIsRecordedForTheSettings()
    {
        var look = await NewService().LookAsync(7);

        Assert.False(look.HasBaseline);
        _watchdog.DidNotReceive().RecordDrift(Arg.Any<IReadOnlyList<SettingDrift>>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task WithABaseline_TheLookRecordsWhatHadDrifted_AndListsIt()
    {
        var baseline = new BaselineSnapshot(Now.AddDays(-30), new Dictionary<string, int?> { ["ads"] = 0 });
        var current = new Dictionary<string, int?> { ["ads"] = 1 };
        var drift = new SettingDrift(Ads, 0, 1);
        _watchdog.LoadBaseline().Returns(baseline);
        _watchdog.ReadCurrent().Returns(current);
        _watchdog.DetectDrift(baseline, current).Returns([drift]);
        _watchdog.RecordDrift(Arg.Any<IReadOnlyList<SettingDrift>>(), Arg.Any<DateTime>())
            .Returns(new DriftSightings([new DriftSighting("ads", 0, 1, Now.AddHours(-5), null)], Readable: true));

        var look = await NewService().LookAsync(7);

        Assert.True(look.HasBaseline);
        _watchdog.Received(1).RecordDrift(Arg.Is<IReadOnlyList<SettingDrift>>(d => d.Count == 1 && d[0] == drift), Now);
        var change = Assert.Single(look.Changes);
        Assert.Equal((ChangeKind.SettingChanged, "Advertising ID", Now.AddHours(-5)), (change.Kind, change.Subject, change.When));
    }

    [Fact]
    public async Task TheFirstLookAtThePrograms_IsSaid()
    {
        _programs.Observe(Arg.Any<DateTime>()).Returns(new ProgramsLook([], FirstLook: true, Readable: true));

        Assert.True((await NewService().LookAsync(7)).FirstProgramsLook);
    }

    [Fact]
    public async Task ProblemsOutsideThePeriod_AreLeftOut()
    {
        Windows(
            new ReliabilityRecord(Now.AddDays(-1), "Application Error", 1000, "contoso.exe", "Faulting application name: contoso.exe"),
            new ReliabilityRecord(Now.AddDays(-30), "Application Hang", 1002, "contoso.exe", "stopped interacting with Windows"));

        var look = await NewService().LookAsync(7);

        Assert.Equal(new ProblemEvent(Now.AddDays(-1), StoppedResponding: false), Assert.Single(look.Problems));
    }

    // ── Each change once, by the best record of it ─────────────────────────

    [Fact]
    public void AWindowsInstallerRecord_WinsOverNewAppAlertsDetectionOfTheSameProgram()
    {
        var installed = Change(ChangeKind.ProgramInstalled, "Contoso Notes", Now);
        var detected = Change(ChangeKind.ProgramDetected, "contoso notes", Now.AddMinutes(10));

        Assert.Equal([installed], RecentChangesService.Combine([installed], [detected], [], []));
    }

    [Fact]
    public void ADetectionWellAwayFromTheInstallerRecord_IsKept()
    {
        var installed = Change(ChangeKind.ProgramInstalled, "Contoso Notes", Now);
        var detected = Change(ChangeKind.ProgramDetected, "Contoso Notes", Now.AddMinutes(11));

        Assert.Equal([detected, installed], RecentChangesService.Combine([installed], [detected], [], []));
    }

    [Fact]
    public void ADifferenceBetweenTwoLooks_GivesWayToARecordInsideItsWindow()
    {
        var appeared = Change(ChangeKind.ProgramAppeared, "Contoso Notes", Now, since: Now.AddDays(-3));
        var installed = Change(ChangeKind.ProgramInstalled, "Contoso Notes", Now.AddDays(-1));
        var detected = Change(ChangeKind.ProgramDetected, "Contoso Notes", Now.AddDays(-2));

        Assert.Equal([installed], RecentChangesService.Combine([installed], [], [appeared], []));
        Assert.Equal([detected], RecentChangesService.Combine([], [detected], [appeared], []));
    }

    [Fact]
    public void ADifferenceBetweenTwoLooks_IsKept_WhenTheOtherRecordIsOutsideItsWindow()
    {
        var appeared = Change(ChangeKind.ProgramAppeared, "Contoso Notes", Now, since: Now.AddDays(-3));
        var installedEarlier = Change(ChangeKind.ProgramInstalled, "Contoso Notes", Now.AddDays(-4));

        Assert.Equal([appeared, installedEarlier], RecentChangesService.Combine([installedEarlier], [], [appeared], []));
    }

    [Fact]
    public void AProgramGoneBetweenTwoLooks_GivesWayToWindowsInstallersRecordOfTheRemoval()
    {
        var gone = Change(ChangeKind.ProgramDisappeared, "Contoso Notes", Now, since: Now.AddDays(-3));
        var removed = Change(ChangeKind.ProgramRemoved, "Contoso Notes", Now.AddDays(-1));
        var installed = Change(ChangeKind.ProgramInstalled, "Contoso Notes", Now.AddDays(-1));

        Assert.Equal([removed], RecentChangesService.Combine([removed], [], [gone], []));
        // An install inside the window says nothing about a removal.
        Assert.Equal([gone, installed], RecentChangesService.Combine([installed], [], [gone], []));
    }

    [Fact]
    public void ChangedSettingsAndOtherChanges_AreAllKept_NewestFirst()
    {
        var update = Change(ChangeKind.WindowsUpdate, "KB5065426", Now.AddHours(-3));
        var action = Change(ChangeKind.SysManagerAction, "Turned on", Now.AddHours(-1));
        var setting = Change(ChangeKind.SettingChanged, "Advertising ID", Now.AddHours(-2));

        Assert.Equal([action, setting, update], RecentChangesService.Combine([update, action], [], [], [setting]));
    }

    // ── The words each row says ─────────────────────────────────────────────

    [Fact]
    public void SysManagersOwnAction_SaysWhatWasDone_ThenWhichTabDidIt()
    {
        var change = Assert.Single(RecentChangesService.Activity([new ActivityEntry("Deep Cleanup", "Freed 1.2 GB", Now)]));

        Assert.Equal((ChangeKind.SysManagerAction, "Freed 1.2 GB", "You, in SysManager", "Deep Cleanup"),
            (change.Kind, change.Subject, change.Who, change.Detail));
    }

    [Fact]
    public void SysManagersOwnAction_WithNoDetail_SaysTheAction()
    {
        var change = Assert.Single(RecentChangesService.Activity([new ActivityEntry("Settings Watchdog", "", Now)]));

        Assert.Equal(("Settings Watchdog", ""), (change.Subject, change.Detail));
    }

    [Fact]
    public void ANewFolder_SaysWhereItAppeared()
    {
        var change = RecentChangesService.Detected(new AppInstallEntry
        {
            Name = "Search Pro",
            InstallPath = @"C:\Program Files\Search Pro",
            DetectedAt = Now,
            Source = "FileSystem",
        });

        Assert.Equal((ChangeKind.ProgramDetected, "Search Pro", "An installer", @"A new folder in C:\Program Files, noticed by New App Alerts"),
            (change.Kind, change.Subject, change.Who, change.Detail));
    }

    [Theory]
    [InlineData("Registry", @"C:\Program Files\Search Pro")]
    [InlineData("FileSystem", "")]
    public void ADetectionWithNoFolderToName_SaysNewAppAlertsNoticedIt(string source, string path)
    {
        var change = RecentChangesService.Detected(new AppInstallEntry { Name = "Search Pro", InstallPath = path, Source = source });

        Assert.Equal("Noticed by New App Alerts while it was watching", change.Detail);
    }

    [Theory]
    [InlineData(ChangeKind.ProgramAppeared, "An installer", "Installed some time between your look on Fri 2 Oct and 15:20")]
    [InlineData(ChangeKind.ProgramDisappeared, "An uninstaller", "Removed some time between your look on Fri 2 Oct and 15:20")]
    [InlineData(ChangeKind.ProgramUpdated, "An installer", "Was Contoso Notes 1.0; updated some time between your look on Fri 2 Oct and 15:20")]
    public void ADifferenceBetweenTwoLooks_SaysTheWindowItHappenedIn(ChangeKind kind, string who, string detail)
    {
        var since = new DateTime(2026, 10, 2, 9, 0, 0);
        var until = new DateTime(2026, 10, 5, 15, 20, 0);

        var change = RecentChangesService.Program(new ProgramChange("Contoso Notes 2.0", "Contoso", kind, since, until, "Contoso Notes 1.0"));

        Assert.Equal((until, since, kind, "Contoso Notes 2.0", who, detail),
            (change.When, change.Since, change.Kind, change.Subject, change.Who, change.Detail));
    }

    [Fact]
    public void AChangedSetting_SaysWhatItIsNow_AndWhatWasSaved()
    {
        var change = Assert.Single(RecentChangesService.Settings(
            [new DriftSighting("ads", 0, 1, new DateTime(2026, 10, 7, 18, 30, 0), null)], [Ads]));

        Assert.Equal(ChangeKind.SettingChanged, change.Kind);
        Assert.Equal("Advertising ID", change.Subject);
        Assert.Equal("Windows or another program", change.Who);
        Assert.Equal("Found On, where you saved Off. Settings Watchdog noticed at 18:30.", change.Detail);
        Assert.Equal(new DateTime(2026, 10, 7, 18, 30, 0), change.When);
    }

    [Fact]
    public void AChangedSettingThatWentBack_SaysSince_When()
    {
        var change = Assert.Single(RecentChangesService.Settings(
            [new DriftSighting("ads", 0, 1, new DateTime(2026, 10, 7, 18, 30, 0), new DateTime(2026, 10, 8, 9, 0, 0))], [Ads]));

        Assert.EndsWith(" It is back as you saved it since Thu 8 Oct.", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASightingOfASettingNoLongerWatched_IsLeftOut()
    {
        Assert.Empty(RecentChangesService.Settings([new DriftSighting("gone", 0, 1, Now, null)], [Ads]));
    }

    [Fact]
    public void DaysAndTimes_ReadTheSameInEveryLanguage()
    {
        Assert.Equal("Thu 8 Oct", RecentChangesService.Day(Now));
        Assert.Equal("15:00", RecentChangesService.Time(Now));
    }
}
