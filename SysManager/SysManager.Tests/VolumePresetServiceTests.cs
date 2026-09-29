// SysManager · VolumePresetServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Linq;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="VolumePresetService"/> — JSON round-trip, name-keyed upsert, exe-name
/// extraction, and the apply-plan that maps a preset onto live sessions by executable name.
/// <para>Mostly pure logic. The exception is the pair of race tests at the bottom, which need the
/// real file to prove that two overlapping mutators cannot lose a preset between them; those inject
/// a temp directory, so the developer's own presets in %LOCALAPPDATA% are never read or written.</para>
/// </summary>
public class VolumePresetServiceTests : IDisposable
{
    private readonly string _dir;

    public VolumePresetServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerPresetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private static VolumePreset Preset(string name, params (string exe, float vol, bool mute)[] apps)
        => new(name, apps.Select(a => new VolumePresetEntry(a.exe, a.exe, a.vol, a.mute)).ToList());

    // The saved presets once the file could be read. Null is its own answer, "could not be read" (#2521).
    private static IReadOnlyList<VolumePreset> Loaded(VolumePresetService service)
    {
        var loaded = service.Load();
        Assert.NotNull(loaded);
        return loaded;
    }

    // ── Serialize / Parse round-trip ───────────────────────────────────────

    [Fact]
    public void SerializeParse_RoundTrips()
    {
        var presets = new[]
        {
            Preset("Gaming", ("game.exe", 1.0f, false), ("spotify.exe", 0.2f, false)),
            Preset("Focus", ("chrome.exe", 0.5f, true)),
        };

        var json = VolumePresetService.Serialize(presets);
        var parsed = VolumePresetService.Parse(json);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Gaming", parsed[0].Name);
        Assert.Equal(2, parsed[0].Entries.Count);
        Assert.Equal("spotify.exe", parsed[0].Entries[1].ExecutableName);
        Assert.Equal(0.2f, parsed[0].Entries[1].Volume);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not json")]
    [InlineData("{ oops")]
    public void Parse_BlankOrMalformed_ReturnsEmpty(string? json)
        => Assert.Empty(VolumePresetService.Parse(json));

    // ── Upsert (add vs replace by name, case-insensitive) ──────────────────

    [Fact]
    public void Upsert_AppendsNewName()
    {
        var existing = new[] { Preset("A", ("a.exe", 1f, false)) };
        var result = VolumePresetService.Upsert(existing, Preset("B", ("b.exe", 0.5f, false)));
        Assert.Equal(2, result.Count);
        Assert.Equal("B", result[1].Name);
    }

    [Fact]
    public void Upsert_ReplacesSameName_CaseInsensitive_InPlace()
    {
        var existing = new[]
        {
            Preset("Gaming", ("old.exe", 1f, false)),
            Preset("Focus", ("f.exe", 0.5f, false)),
        };

        var result = VolumePresetService.Upsert(existing, Preset("gaming", ("new.exe", 0.3f, true)));

        Assert.Equal(2, result.Count);                 // replaced, not appended
        Assert.Equal("gaming", result[0].Name);        // in place at index 0
        Assert.Equal("new.exe", result[0].Entries[0].ExecutableName);
        Assert.Equal("Focus", result[1].Name);         // sibling untouched
    }

    [Fact]
    public void Upsert_DoesNotMutateInput()
    {
        var existing = new List<VolumePreset> { Preset("A", ("a.exe", 1f, false)) };
        VolumePresetService.Upsert(existing, Preset("B", ("b.exe", 1f, false)));
        Assert.Single(existing); // original list unchanged
    }

    // ── ExeName ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Program Files\Game\game.exe", "game.exe")]
    [InlineData(@"D:\apps\spotify.exe", "spotify.exe")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ExeName_ExtractsFileName(string? path, string expected)
        => Assert.Equal(expected, VolumePresetService.ExeName(path));

    // ── BuildApplyPlan (map preset → live sessions by exe name) ────────────

    private static AudioSessionInfo Session(string sessionId, string exePath)
        => new(sessionId, 100, exePath, exePath, 0f, false, AudioSessionState.Active, false, 0f);

    [Fact]
    public void BuildApplyPlan_MatchesByExeName_CaseInsensitive()
    {
        var preset = Preset("Gaming", ("game.exe", 0.9f, false), ("spotify.exe", 0.1f, true));
        var live = new[]
        {
            Session("s1", @"C:\Games\GAME.EXE"),   // matches game.exe (case-insensitive)
            Session("s2", @"C:\Music\spotify.exe"),
            Session("s3", @"C:\Other\notepad.exe"), // no preset entry → not in plan
        };

        var plan = VolumePresetService.BuildApplyPlan(preset, live);

        Assert.Equal(2, plan.Count);
        var game = plan.First(p => p.SessionId == "s1");
        Assert.Equal(0.9f, game.Volume);
        Assert.False(game.IsMuted);
        var spotify = plan.First(p => p.SessionId == "s2");
        Assert.Equal(0.1f, spotify.Volume);
        Assert.True(spotify.IsMuted);
        Assert.DoesNotContain(plan, p => p.SessionId == "s3");
    }

    [Fact]
    public void BuildApplyPlan_ClampsVolumeIntoRange()
    {
        var preset = Preset("Weird", ("a.exe", 5f, false), ("b.exe", -1f, false));
        var live = new[] { Session("s1", @"x\a.exe"), Session("s2", @"x\b.exe") };

        var plan = VolumePresetService.BuildApplyPlan(preset, live);

        Assert.Equal(1f, plan.First(p => p.SessionId == "s1").Volume);   // clamped high
        Assert.Equal(0f, plan.First(p => p.SessionId == "s2").Volume);   // clamped low
    }

    [Fact]
    public void BuildApplyPlan_NoMatches_ReturnsEmpty()
    {
        var preset = Preset("X", ("nothere.exe", 0.5f, false));
        var plan = VolumePresetService.BuildApplyPlan(preset, [Session("s1", @"x\other.exe")]);
        Assert.Empty(plan);
    }

    // ── A presets file that cannot be read, parsed or written (#2521) ─────────
    //
    // A file that could not be read loaded as no presets, so Save wrote only its own preset over every other and
    // Delete wrote none. Held open with delete sharing only, a read fails and a write would still succeed; held
    // with read sharing only, a read succeeds and the write fails.

    private string PresetsFile => Path.Combine(_dir, "volume-presets.json");

    private FileStream Hold(FileShare share) => new(PresetsFile, FileMode.Open, FileAccess.Read, share);

    [Fact]
    public void Load_WhenTheFileCannotBeRead_IsNull_NotEmpty()
    {
        var service = new VolumePresetService(_dir);
        Assert.NotNull(service.Save(Preset("Gaming", ("game.exe", 0.9f, false))));

        using (Hold(FileShare.Delete))
            Assert.Null(service.Load());

        Assert.Single(Loaded(service));   // readable again once the file is released
    }

    [Fact]
    public void Save_WhenTheFileCannotBeRead_WritesNothing_AndReturnsNull()
    {
        var service = new VolumePresetService(_dir);
        Assert.NotNull(service.Save(Preset("Gaming", ("game.exe", 0.9f, false))));
        Assert.NotNull(service.Save(Preset("Focus", ("chrome.exe", 0.2f, true))));

        using (Hold(FileShare.Delete))
            Assert.Null(service.Save(Preset("Movie night", ("vlc.exe", 0.6f, false))));

        Assert.Equal(["Gaming", "Focus"], Loaded(service).Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Delete_WhenTheFileCannotBeRead_WritesNothing_AndReturnsNull()
    {
        var service = new VolumePresetService(_dir);
        Assert.NotNull(service.Save(Preset("Gaming", ("game.exe", 0.9f, false))));
        Assert.NotNull(service.Save(Preset("Focus", ("chrome.exe", 0.2f, true))));

        using (Hold(FileShare.Delete))
            Assert.Null(service.Delete("Gaming"));

        Assert.Equal(["Gaming", "Focus"], Loaded(service).Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Save_WhenTheWriteFails_ReturnsNull_RatherThanTheListItCouldNotSave()
    {
        // The tab used to say "Saved preset" whatever happened, because Persist swallowed the failure.
        var service = new VolumePresetService(_dir);
        Assert.NotNull(service.Save(Preset("Gaming", ("game.exe", 0.9f, false))));

        using (Hold(FileShare.Read))
            Assert.Null(service.Save(Preset("Focus", ("chrome.exe", 0.2f, true))));

        Assert.Equal(["Gaming"], Loaded(service).Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Save_OverAFileThatDoesNotParse_KeepsItAside_ThenSaves()
    {
        File.WriteAllText(PresetsFile, "{ not a preset list");
        var service = new VolumePresetService(_dir);

        Assert.NotNull(service.Save(Preset("Gaming", ("game.exe", 0.9f, false))));

        Assert.Equal("{ not a preset list", File.ReadAllText(PresetsFile + ".unreadable"));
        Assert.Equal("Gaming", Assert.Single(Loaded(service)).Name);
    }

    [Fact]
    public void Save_WhenAFileThatDoesNotParseCannotBeSetAside_WritesNothing()
    {
        File.WriteAllText(PresetsFile, "{ not a preset list");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(PresetsFile + ".unreadable");
        var service = new VolumePresetService(_dir);

        Assert.Null(service.Save(Preset("Gaming", ("game.exe", 0.9f, false))));

        Assert.Equal("{ not a preset list", File.ReadAllText(PresetsFile));
    }

    [Fact]
    public void Delete_OverAFileThatDoesNotParse_KeepsItAside()
    {
        File.WriteAllText(PresetsFile, "{ not a preset list");
        var service = new VolumePresetService(_dir);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<VolumePreset>>(service.Delete("Gaming")));

        Assert.Equal("{ not a preset list", File.ReadAllText(PresetsFile + ".unreadable"));
        Assert.Empty(Loaded(service));
    }

    [Fact]
    public void Delete_WhenAFileThatDoesNotParseCannotBeSetAside_WritesNothing()
    {
        File.WriteAllText(PresetsFile, "{ not a preset list");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(PresetsFile + ".unreadable");
        var service = new VolumePresetService(_dir);

        Assert.Null(service.Delete("Gaming"));

        Assert.Equal("{ not a preset list", File.ReadAllText(PresetsFile));
    }

    // ── the two mutators racing each other ─────────────────────────────────

    /// <summary>
    /// How many times each race below is run. One attempt is a directory plus two small atomic
    /// writes, so the whole cost is a fraction of a second. The count exists because the
    /// unsynchronized code only loses a preset on the interleaving where the second writer's save
    /// lands after reading a snapshot that predates the first — about half of them — so a single
    /// attempt could pass straight over the bug. Once the read-modify-write pairs are serialized,
    /// EVERY attempt passes by construction (both orders end with both presets), so the repetition
    /// cannot make these flaky.
    /// </summary>
    private const int RaceAttempts = 64;

    /// <summary>
    /// Runs <paramref name="first"/> and <paramref name="second"/> against one service instance from
    /// a shared start line, then hands the persisted list to <paramref name="assert"/>. Bounded at
    /// both ends: a writer that never reaches the line, or never returns, fails the run instead of
    /// hanging it.
    /// </summary>
    private async Task RaceTwoMutators(
        Action<VolumePresetService> first,
        Action<VolumePresetService> second,
        Action<int, IReadOnlyList<VolumePreset>> assert)
    {
        for (var attempt = 0; attempt < RaceAttempts; attempt++)
        {
            var dir = Path.Combine(_dir, $"race-{attempt}");
            Directory.CreateDirectory(dir);
            var service = new VolumePresetService(dir);

            await StartLine.RaceAsync(() => first(service), () => second(service));

            assert(attempt, Loaded(new VolumePresetService(dir)));
        }
    }

    [Fact]
    public async Task TwoPresetsSavedAtOnce_BothSurvive()
    {
        // Save is a Load followed by a Persist over one file holding EVERY preset, so it writes back
        // a whole-list snapshot. AtomicFile makes each write atomic but not the pair: unsynchronized,
        // the second writer can persist a list it built before the first landed, and the first preset
        // is gone. Silently — Persist swallows IOException by design, so nothing reports it, and the
        // user finds out when a preset they saved is missing.
        await RaceTwoMutators(
            s => s.Save(Preset("Gaming", ("game.exe", 0.9f, false))),
            s => s.Save(Preset("Focus", ("chrome.exe", 0.2f, true))),
            (attempt, presets) =>
            {
                // No path in the message: a failure here is printed in public CI output.
                Assert.True(presets.Any(p => p.Name == "Gaming"),
                    $"attempt {attempt}: the Gaming preset was dropped — a writer persisted a list "
                        + "it had built before the other landed");
                Assert.True(presets.Any(p => p.Name == "Focus"),
                    $"attempt {attempt}: the Focus preset was dropped — a writer persisted a list "
                        + "it had built before the other landed");
                // The entries, not just the names: a lost write that happened to leave both names
                // present would still restore the wrong levels.
                Assert.Equal(0.9f, presets.First(p => p.Name == "Gaming").Entries[0].Volume);
                Assert.Equal(0.2f, presets.First(p => p.Name == "Focus").Entries[0].Volume);
            });
    }

    [Fact]
    public async Task DeletingOnePresetWhileAnotherIsSaved_DoesNotResurrectTheDeletedOne()
    {
        // The other pairing, so Delete's half of the lock is exercised too. Unsynchronized, Save can
        // write back a list that still contains the deleted preset — and a preset the user deleted
        // coming back is the worse direction of the same bug, because Apply would then set volumes
        // from levels they had already thrown away.
        await RaceTwoMutators(
            s => { s.Save(Preset("Gaming", ("game.exe", 0.9f, false))); s.Delete("Gaming"); },
            s => s.Save(Preset("Focus", ("chrome.exe", 0.2f, true))),
            (attempt, presets) =>
            {
                Assert.DoesNotContain("Gaming", presets.Select(p => p.Name));
                Assert.True(presets.Any(p => p.Name == "Focus"),
                    $"attempt {attempt}: the Focus preset was dropped by the overlapping Delete");
            });
    }
}
