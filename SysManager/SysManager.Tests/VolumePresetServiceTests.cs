// SysManager · VolumePresetServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Linq;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="VolumePresetService"/>'s pure logic — JSON round-trip, name-keyed upsert,
/// exe-name extraction, and the apply-plan that maps a preset onto live sessions by executable
/// name. No file IO is exercised here (the persistence path is thin file read/write).
/// </summary>
public class VolumePresetServiceTests
{
    private static VolumePreset Preset(string name, params (string exe, float vol, bool mute)[] apps)
        => new(name, apps.Select(a => new VolumePresetEntry(a.exe, a.exe, a.vol, a.mute)).ToList());

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
}
