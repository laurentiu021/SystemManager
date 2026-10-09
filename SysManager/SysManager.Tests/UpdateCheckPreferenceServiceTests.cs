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

    /// <summary>
    /// What a fresh instance loads, as after a restart, asserted to have been readable. Null is its own answer, "could
    /// not be read" (#2614).
    /// </summary>
    private UpdateCheckPreference Loaded(string? dir = null)
    {
        var preference = new UpdateCheckPreferenceService(dir ?? _dir).Load();
        Assert.NotNull(preference);
        return preference;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    // ── Defaults ────────────────────────────────────────────────────────────

    [Fact]
    public void WithNothingSaved_TheCheckIsEnabledAndHasNeverRun()
    {
        // Enabled by default on purpose: an update check is how someone on an unsigned,
        // self-distributed build learns about a security fix, so silence is the worse default.
        var pref = Loaded();
        Assert.True(pref.CheckOnStartup);
        Assert.Null(pref.LastCheckUtc);
    }

    [Fact]
    public void WithNothingSaved_TheStartupCheckRuns()
        => Assert.True(UpdateCheckPreferenceService.ShouldCheckAtStartup(
            UpdateCheckPreferenceService.Default, Now));

    // ── A preference file that cannot be read or parsed (#2521) ────────────
    //
    // A file that could not be read loaded as the default, "on", and the check that then ran recorded itself by
    // writing that default back: the user's "off" was gone for good. The file is held open with delete sharing
    // only while a read must fail: the read fails, and the replace a write ends with would still succeed.

    private string PreferenceFile => Path.Combine(_dir, UpdateCheckPreferenceService.FileName);

    // Load answered "on" for both, and the startup check went by it: GitHub was asked for the newest version on a
    // launch where the saved "off" could not be read (#2614).

    [Fact]
    public void Load_WhenThePreferenceCannotBeRead_IsNull()
    {
        NewService().SetCheckOnStartup(false);

        using (new FileStream(PreferenceFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
            Assert.Null(NewService().Load());

        Assert.False(Loaded().CheckOnStartup);   // once it can be read again, the choice is still there
    }

    [Fact]
    public void Load_OverAFileThatDoesNotParse_IsNull_AndLeavesTheFileAsItIs()
    {
        File.WriteAllText(PreferenceFile, "{ not a preference");

        Assert.Null(NewService().Load());

        Assert.Equal("{ not a preference", File.ReadAllText(PreferenceFile));
        Assert.False(File.Exists(PreferenceFile + ".unreadable"));
    }

    [Fact]
    public void RecordCheck_WhenThePreferenceCannotBeRead_KeepsTheUsersOff()
    {
        NewService().SetCheckOnStartup(false);

        using (new FileStream(PreferenceFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
            NewService().RecordCheck(Now);

        var pref = Loaded();
        Assert.False(pref.CheckOnStartup);
        Assert.Null(pref.LastCheckUtc);   // nothing was written
    }

    [Fact]
    public void RecordCheck_OverAFileThatDoesNotParse_KeepsItAside_AndRecords()
    {
        File.WriteAllText(PreferenceFile, "{ not a preference");

        NewService().RecordCheck(Now);

        Assert.Equal("{ not a preference", File.ReadAllText(PreferenceFile + ".unreadable"));
        Assert.Equal(Now, Loaded().LastCheckUtc);
    }

    [Fact]
    public void SetCheckOnStartup_OverAFileThatDoesNotParse_KeepsItAside_AndSaves()
    {
        File.WriteAllText(PreferenceFile, "{ not a preference");

        NewService().SetCheckOnStartup(false);

        Assert.Equal("{ not a preference", File.ReadAllText(PreferenceFile + ".unreadable"));
        Assert.False(Loaded().CheckOnStartup);
    }

    // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not. A
    // sharing hold would fail the write as well, and could not tell a refusal from a write that failed.

    [Fact]
    public void RecordCheck_WhenAFileThatDoesNotParseCannotBeSetAside_WritesNothing()
    {
        File.WriteAllText(PreferenceFile, "{ not a preference");
        Directory.CreateDirectory(PreferenceFile + ".unreadable");

        NewService().RecordCheck(Now);

        Assert.Equal("{ not a preference", File.ReadAllText(PreferenceFile));
    }

    [Fact]
    public void SetCheckOnStartup_WhenAFileThatDoesNotParseCannotBeSetAside_StillSavesTheChoice()
    {
        // The user's choice wins over keeping a damaged file: all it can hold besides is when the last check ran.
        File.WriteAllText(PreferenceFile, "{ not a preference");
        Directory.CreateDirectory(PreferenceFile + ".unreadable");

        NewService().SetCheckOnStartup(false);

        Assert.False(Loaded().CheckOnStartup);
    }

    // ── The on/off switch ───────────────────────────────────────────────────

    [Fact]
    public void TurningTheCheckOff_PersistsAcrossInstances()
    {
        NewService().SetCheckOnStartup(false);

        // A second instance, as after a restart — the point of persisting at all.
        Assert.False(Loaded().CheckOnStartup);
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
        Assert.True(Loaded().CheckOnStartup);
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

        Assert.Equal(Now, Loaded().LastCheckUtc);
    }

    // ── The 24h throttle ────────────────────────────────────────────────────

    [Fact]
    public void RecordingACheck_PersistsWhenItRan()
    {
        NewService().RecordCheck(Now);

        var pref = Loaded();
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
        // Parse always has an answer. Load does not take it for a file that does not parse: that file may have held
        // "off", so it loads as null and the startup check does not run (#2614).
        var pref = UpdateCheckPreferenceService.Parse(json);
        Assert.True(pref.CheckOnStartup);
        Assert.Null(pref.LastCheckUtc);
    }

    [Fact]
    public void AFolderWhereTheFileShouldBe_CountsAsNothingSaved()
    {
        // File.Exists is false for a folder, so it reads as no file at all: it holds no choice that could be "off",
        // the default applies, and Load does not throw into app startup.
        Directory.CreateDirectory(Path.Combine(_dir, UpdateCheckPreferenceService.FileName));
        Assert.True(Loaded().CheckOnStartup);
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

        Assert.False(Loaded().CheckOnStartup);
        Assert.True(Loaded(other).CheckOnStartup);
    }

    // ── The two mutators racing each other ──────────────────────────────────

    /// <summary>
    /// How many times the race below is run. Each attempt is one directory plus two small atomic
    /// writes, so the whole test costs a fraction of a second. The count exists because the
    /// unsynchronized code only loses the update on the interleaving where <c>RecordCheck</c>'s
    /// save lands last — about half of them — so a single attempt could pass over the bug. Once the
    /// read-modify-write pairs are serialized, EVERY attempt passes by construction (both orders
    /// end with the user's choice), so the repetition cannot make this flaky.
    /// </summary>
    private const int RaceAttempts = 64;

    [Fact]
    public async Task RecordingACheckWhileTheUserTurnsItOff_KeepsTheChoiceOff()
    {
        // The live race, on the one instance About holds: the startup check calls RecordCheck from
        // a continuation once the GitHub calls return, while the user's tick calls
        // SetCheckOnStartup on the UI thread. Both are a Load followed by a Save over the same
        // file. AtomicFile makes each WRITE atomic but not the read-then-write pair, so whichever
        // saves last persists a snapshot taken before the other landed — and when that is
        // RecordCheck, the "off" the user just chose silently comes back on.
        for (var attempt = 0; attempt < RaceAttempts; attempt++)
        {
            var dir = Path.Combine(_dir, $"race-{attempt}");
            Directory.CreateDirectory(dir);
            var service = new UpdateCheckPreferenceService(dir);

            await StartLine.RaceAsync(() => service.RecordCheck(Now), () => service.SetCheckOnStartup(false));

            // No path in the message: a failure here is printed in public CI output.
            Assert.False(
                Loaded(dir).CheckOnStartup,
                $"attempt {attempt}: the startup check came back on — a mutator saved a snapshot "
                    + "it had taken before the user's choice landed");
        }
    }
}
