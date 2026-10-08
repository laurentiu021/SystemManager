// SysManager · SettingsDriftSightingsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// When Settings Watchdog first saw each changed setting, and when it went back (#1507): the record that lets Recent
/// Changes say when a setting changed. In a temp folder, so the user's own baseline and record are never touched.
/// </summary>
public sealed class SettingsDriftSightingsTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 18, 30, 0);
    private static readonly DateTime Tuesday = Monday.AddDays(1);
    private static readonly DateTime Wednesday = Monday.AddDays(2);

    private readonly string _dir;

    public SettingsDriftSightingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerWatchdogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    private SettingsWatchdogService NewService() => new(_dir);

    private string SightingsFile => Path.Combine(_dir, "settings-drift.json");

    private static WatchedSetting Setting(string key) => new(
        key, $"Name {key}", "desc", "Privacy", $@"HKCU\SOFTWARE\Test\{key}", "Val",
        new Dictionary<int, string> { [0] = "Off", [1] = "On" });

    private static SettingDrift Drift(string key, int? baseline = 0, int? now = 1) => new(Setting(key), baseline, now);

    private static DriftSighting Sighting(string key, DateTime firstSeen, DateTime? goneAt = null, int? baseline = 0, int? value = 1) =>
        new(key, baseline, value, firstSeen, goneAt);

    // ── What one look does to the record ─────────────────────────────────

    [Fact]
    public void ADriftSeenForTheFirstTime_IsRecordedAtThatLook()
    {
        var sightings = SettingsWatchdogService.UpdateSightings([], [Drift("ads")], Monday);

        Assert.Equal([Sighting("ads", Monday)], sightings);
    }

    [Fact]
    public void TheSameDriftSeenAgain_KeepsWhenItWasFirstSeen()
    {
        var sightings = SettingsWatchdogService.UpdateSightings([Sighting("ads", Monday)], [Drift("ads")], Tuesday);

        Assert.Equal([Sighting("ads", Monday)], sightings);
    }

    [Fact]
    public void ADriftThatHasGone_IsMarkedGoneAtTheLookThatFoundItGone()
    {
        var sightings = SettingsWatchdogService.UpdateSightings([Sighting("ads", Monday)], [], Tuesday);

        Assert.Equal([Sighting("ads", Monday, goneAt: Tuesday)], sightings);
    }

    [Fact]
    public void ADriftThatComesBack_IsSeenAgain_AsNew()
    {
        var sightings = SettingsWatchdogService.UpdateSightings(
            [Sighting("ads", Monday, goneAt: Tuesday)], [Drift("ads")], Wednesday);

        Assert.Equal([Sighting("ads", Monday, goneAt: Tuesday), Sighting("ads", Wednesday)], sightings);
    }

    [Fact]
    public void ADriftToAnotherValue_EndsTheOldSighting_AndStartsANewOne()
    {
        var sightings = SettingsWatchdogService.UpdateSightings([Sighting("ads", Monday)], [Drift("ads", now: 2)], Tuesday);

        Assert.Equal([Sighting("ads", Monday, goneAt: Tuesday), Sighting("ads", Tuesday, value: 2)], sightings);
    }

    [Fact]
    public void ASightingThatEndedLongerAgoThanRecentChangesLooksBack_IsDropped()
    {
        var ended = Sighting("ads", Monday, goneAt: Tuesday);

        var kept = SettingsWatchdogService.UpdateSightings([ended], [], Tuesday + RecentChangesService.KeptFor);
        var dropped = SettingsWatchdogService.UpdateSightings([ended], [], Tuesday + RecentChangesService.KeptFor + TimeSpan.FromSeconds(1));

        Assert.Equal([ended], kept);
        Assert.Empty(dropped);
    }

    [Fact]
    public void ASightingStillOpen_IsKeptHoweverOld()
    {
        // It is what stops the same drift being reported again as new.
        var open = Sighting("ads", Monday);

        var sightings = SettingsWatchdogService.UpdateSightings([open], [Drift("ads")], Monday.AddYears(1));

        Assert.Equal([open], sightings);
    }

    // ── The record on disk ─────────────────────────────────────────────────

    [Fact]
    public void RecordDrift_KeepsWhatItSaw_ForTheNextLook()
    {
        NewService().RecordDrift([Drift("ads")], Monday);

        var next = NewService().RecordDrift([Drift("ads")], Tuesday);

        Assert.True(next.Readable);
        Assert.Equal([Sighting("ads", Monday)], next.Sightings);
    }

    [Fact]
    public void RecordDrift_WhenNothingChanged_WritesNothing()
    {
        NewService().RecordDrift([Drift("ads")], Monday);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(SightingsFile, stamp);

        NewService().RecordDrift([Drift("ads")], Tuesday);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(SightingsFile));
    }

    [Fact]
    public void RecordDrift_WithNoDriftAndNothingRecorded_WritesNoFile()
    {
        var read = NewService().RecordDrift([], Monday);

        Assert.True(read.Readable);
        Assert.Empty(read.Sightings);
        Assert.False(File.Exists(SightingsFile));
    }

    [Fact]
    public void RecordDrift_WhenTheRecordCannotBeRead_SaysSo_AndWritesNothing()
    {
        NewService().RecordDrift([Drift("ads")], Monday);
        var before = File.ReadAllBytes(SightingsFile);

        DriftSightings read;
        using (new FileStream(SightingsFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
            read = NewService().RecordDrift([], Tuesday);

        Assert.Same(DriftSightings.Unreadable, read);
        Assert.Equal(before, File.ReadAllBytes(SightingsFile));
    }

    [Fact]
    public void RecordDrift_OverARecordThatDoesNotParse_SetsItAside_AndStartsANewOne()
    {
        File.WriteAllText(SightingsFile, "{ not json");

        var read = NewService().RecordDrift([Drift("ads")], Monday);

        Assert.Equal([Sighting("ads", Monday)], read.Sightings);
        Assert.Equal("{ not json", File.ReadAllText(SightingsFile + ".unreadable"));
    }

    [Fact]
    public void RecordDrift_OverARecordThatDoesNotParse_AndCannotBeSetAside_LeavesIt()
    {
        File.WriteAllText(SightingsFile, "{ not json");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(SightingsFile + ".unreadable");

        Assert.Same(DriftSightings.Unreadable, NewService().RecordDrift([Drift("ads")], Monday));
        Assert.Equal("{ not json", File.ReadAllText(SightingsFile));
    }

    [Fact]
    public void SavingABaseline_ForgetsTheSightings()
    {
        // They were measured against the baseline it replaces, so they no longer mean anything.
        NewService().RecordDrift([Drift("ads")], Monday);
        Assert.True(File.Exists(SightingsFile));

        NewService().SaveBaseline(Tuesday);

        Assert.False(File.Exists(SightingsFile));
        Assert.Empty(NewService().RecordDrift([], Wednesday).Sightings);
    }

    [Fact]
    public void RecordDrift_RefusesNoDrifts()
    {
        Assert.Throws<ArgumentNullException>(() => NewService().RecordDrift(null!, Monday));
    }

    [Fact]
    public void ParseSightings_DropsOnesWithNoSetting()
    {
        var json = JsonSerializer.Serialize(new[] { Sighting("ads", Monday), Sighting("", Monday) });

        Assert.Equal([Sighting("ads", Monday)], SettingsWatchdogService.ParseSightings(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ not json")]
    public void ParseSightings_OfSomethingThatIsNotAList_IsNull(string json)
    {
        Assert.Null(SettingsWatchdogService.ParseSightings(json));
    }
}
