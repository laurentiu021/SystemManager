// SysManager · UpdateCheckPreferenceServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System;
using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UpdateCheckPreferenceService"/> — the gate on the app's only outbound call.
/// <para>Before this, two requests went to api.github.com on EVERY launch (latest release, plus the
/// last ten) with no setting, no UI and no record of the previous check. So the "no telemetry,
/// fully local" claim had an exception the user could neither see nor switch off, and restarting
/// repeatedly could exhaust GitHub's anonymous limit (60/hour/IP) until About showed an error for
/// no real reason.</para>
/// <para>Every test injects a temp directory, so the developer's own preference file is never read
/// or written. The throttle decision is a pure static, so its clock is a parameter rather than
/// <c>DateTimeOffset.UtcNow</c> — no sleeping, no wall-clock flakiness.</para>
/// </summary>
public sealed class UpdateCheckPreferenceServiceTests : IDisposable
{
    private readonly string _dir;

    public UpdateCheckPreferenceServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerUpdateCheckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        GC.SuppressFinalize(this);
    }

    private UpdateCheckPreferenceService NewService() => new(_dir);

    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    // ── Defaults ────────────────────────────────────────────────────────────

    [Fact]
    public void WithNothingSaved_TheCheckIsEnabledAndHasNeverRun()
    {
        // Enabled by default on purpose: an update check is how someone on an unsigned,
        // self-distributed build learns about a security fix, so silence is the worse default.
        var pref = NewService().Load();
        Assert.True(pref.CheckOnStartup);
        Assert.Null(pref.LastCheckUtc);
    }

    [Fact]
    public void WithNothingSaved_TheStartupCheckRuns()
        => Assert.True(UpdateCheckPreferenceService.ShouldCheckAtStartup(
            UpdateCheckPreferenceService.Default, Now));

    // ── The on/off switch ───────────────────────────────────────────────────

    [Fact]
    public void TurningTheCheckOff_PersistsAcrossInstances()
    {
        NewService().SetCheckOnStartup(false);

        // A second instance, as after a restart — the point of persisting at all.
        Assert.False(NewService().Load().CheckOnStartup);
    }

    [Fact]
    public void TurningItOff_StopsTheStartupCheckEvenIfItNeverRan()
    {
        var pref = new UpdateCheckPreference(CheckOnStartup: false, LastCheckUtc: null);
        Assert.False(UpdateCheckPreferenceService.ShouldCheckAtStartup(pref, Now));
    }

    [Fact]
    public void TurningTheCheckOffThenOnAgain_RestoresIt()
    {
        var service = NewService();
        service.SetCheckOnStartup(false);
        service.SetCheckOnStartup(true);
        Assert.True(NewService().Load().CheckOnStartup);
    }

    [Fact]
    public void TogglingTheSwitch_KeepsTheLastCheckTimestamp()
    {
        // Otherwise turning the setting off and on again would reset the throttle and let the very
        // next launch call GitHub — a toggle is not a reason to re-check.
        var service = NewService();
        service.RecordCheck(Now);
        service.SetCheckOnStartup(false);
        service.SetCheckOnStartup(true);

        Assert.Equal(Now, NewService().Load().LastCheckUtc);
    }

    // ── The 24h throttle ────────────────────────────────────────────────────

    [Fact]
    public void RecordingACheck_PersistsWhenItRan()
    {
        NewService().RecordCheck(Now);

        var pref = NewService().Load();
        Assert.Equal(Now, pref.LastCheckUtc);
        Assert.True(pref.CheckOnStartup);   // recording must not disturb the user's choice
    }

    [Fact]
    public void ImmediatelyAfterACheck_TheNextStartupSkipsIt()
    {
        // The rate-limit failure mode: repeated restarts each firing two requests.
        var pref = new UpdateCheckPreference(true, Now);
        Assert.False(UpdateCheckPreferenceService.ShouldCheckAtStartup(pref, Now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(23)]
    public void WithinTheWindow_TheStartupCheckIsSkipped(int hoursLater)
    {
        var pref = new UpdateCheckPreference(true, Now);
        Assert.False(UpdateCheckPreferenceService.ShouldCheckAtStartup(pref, Now.AddHours(hoursLater)));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(72)]
    public void OnceTheWindowHasPassed_TheStartupCheckRunsAgain(int hoursLater)
    {
        var pref = new UpdateCheckPreference(true, Now);
        Assert.True(UpdateCheckPreferenceService.ShouldCheckAtStartup(pref, Now.AddHours(hoursLater)));
    }

    [Fact]
    public void TheWindowIsExactlyOneDay()
        => Assert.Equal(TimeSpan.FromHours(24), UpdateCheckPreferenceService.ThrottleWindow);

    [Fact]
    public void AFutureDatedCheck_IsTreatedAsStaleRatherThanTrusted()
    {
        // A clock moved backwards, or a preference file copied from another machine, would
        // otherwise suppress update checks until real time caught up — potentially for years.
        var pref = new UpdateCheckPreference(true, Now.AddDays(30));
        Assert.True(UpdateCheckPreferenceService.ShouldCheckAtStartup(pref, Now));
    }

    // ── Round-trip and robustness ───────────────────────────────────────────

    [Fact]
    public void SerializeThenParse_RoundTripsBothFields()
    {
        var original = new UpdateCheckPreference(CheckOnStartup: false, LastCheckUtc: Now);
        var parsed = UpdateCheckPreferenceService.Parse(UpdateCheckPreferenceService.Serialize(original));

        Assert.False(parsed.CheckOnStartup);
        Assert.Equal(Now, parsed.LastCheckUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ truncated")]
    public void MalformedInput_FallsBackToEnabled(string? json)
    {
        // Deliberately the opposite of ClosePreferenceService, which falls back to "ask". There is
        // nothing to ask here, and defaulting to OFF would quietly close the only channel that
        // tells the user about a fix — a silent failure they would never notice.
        var pref = UpdateCheckPreferenceService.Parse(json);
        Assert.True(pref.CheckOnStartup);
        Assert.Null(pref.LastCheckUtc);
    }

    [Fact]
    public void AnUnreadableFile_FallsBackToEnabled()
    {
        // A directory where the file should be: File.Exists is false for it, so this exercises the
        // guard rather than the read — the point is that Load never throws into app startup.
        Directory.CreateDirectory(Path.Combine(_dir, UpdateCheckPreferenceService.FileName));
        Assert.True(NewService().Load().CheckOnStartup);
    }

    [Fact]
    public void TheFileLandsInTheInjectedDirectory_NotTheRealProfile()
    {
        NewService().SetCheckOnStartup(false);
        Assert.True(File.Exists(Path.Combine(_dir, UpdateCheckPreferenceService.FileName)));
    }

    [Fact]
    public void TwoDirectories_AreIndependent()
    {
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);

        NewService().SetCheckOnStartup(false);

        Assert.False(NewService().Load().CheckOnStartup);
        Assert.True(new UpdateCheckPreferenceService(other).Load().CheckOnStartup);
    }
}
