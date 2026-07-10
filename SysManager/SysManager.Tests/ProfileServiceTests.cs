// SysManager · ProfileServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProfileService"/> — the config export/import bundle. Runs against a
/// temp config directory so it never touches the real %LOCALAPPDATA%\SysManager profile.
/// </summary>
public class ProfileServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly ProfileService _svc;

    public ProfileServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerProfileTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _svc = new ProfileService(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteConfig(string fileName, string json) => File.WriteAllText(Path.Combine(_dir, fileName), json);

    // ---------- AvailableSections ----------

    [Fact]
    public void AvailableSections_OnlyIncludesExistingFiles()
    {
        Assert.Empty(_svc.AvailableSections());

        WriteConfig("theme.json", "{\"preset\":\"midnight\"}");
        var sections = _svc.AvailableSections();
        Assert.Single(sections);
        Assert.Equal("theme", sections[0].Key);
        Assert.Contains("midnight", sections[0].Json);
    }

    // ---------- Serialize / Deserialize round-trip ----------

    [Fact]
    public void BuildAndSerialize_RoundTrips()
    {
        WriteConfig("theme.json", "{\"preset\":\"deep-ocean\"}");
        WriteConfig("speedtest-history.json", "[1,2,3]");

        var profile = _svc.BuildProfile(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local));
        var json = ProfileService.Serialize(profile);
        var restored = ProfileService.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(ProfileService.CurrentSchemaVersion, restored!.SchemaVersion);
        Assert.Equal(2, restored.Sections.Count);
        Assert.Contains(restored.Sections, s => s.Key == "theme" && s.Json.Contains("deep-ocean"));
    }

    [Fact]
    public void Deserialize_GarbageJson_ReturnsNull()
        => Assert.Null(ProfileService.Deserialize("{ not a profile "));

    [Fact]
    public void Deserialize_NewerSchema_Throws()
    {
        var future = $"{{\"SchemaVersion\":{ProfileService.CurrentSchemaVersion + 1},\"AppVersion\":\"9.9.9\",\"ExportedAt\":\"2026-01-01T00:00:00\",\"Sections\":[]}}";
        Assert.Throws<NotSupportedException>(() => ProfileService.Deserialize(future));
    }

    [Fact]
    public void Deserialize_MissingSectionsProperty_YieldsEmptyList_NotNull()
    {
        // Regression (P2 #11): a syntactically-valid profile JSON that OMITS "Sections"
        // (a truncated export, an empty-ish object, or foreign JSON picked in Import)
        // used to deserialize Sections to null — System.Text.Json does not enforce
        // non-null on positional record params — and ProfileViewModel.Import then threw
        // an unhandled NullReferenceException on profile.Sections.Count. Now the model
        // defaults it to [] and Deserialize normalizes it, so callers can always
        // enumerate .Sections safely.
        var json = $"{{\"SchemaVersion\":{ProfileService.CurrentSchemaVersion},\"AppVersion\":\"1.0.0\",\"ExportedAt\":\"2026-01-01T00:00:00\"}}";
        var profile = ProfileService.Deserialize(json);
        Assert.NotNull(profile);
        Assert.NotNull(profile!.Sections);
        Assert.Empty(profile.Sections);
    }

    [Fact]
    public void Deserialize_EmptyObject_YieldsEmptySections()
    {
        // The most degenerate valid JSON object — every property absent — must still
        // produce a usable profile with an empty (non-null) Sections list.
        var profile = ProfileService.Deserialize("{}");
        Assert.NotNull(profile);
        Assert.Empty(profile!.Sections);
    }

    // ---------- ApplySections ----------

    [Fact]
    public void ApplySections_WritesKnownFiles()
    {
        var sections = new[]
        {
            new ConfigSection("theme", "Theme & appearance", "theme.json", "{\"preset\":\"warm-ember\"}"),
        };
        var applied = _svc.ApplySections(sections);

        Assert.Equal(1, applied);
        Assert.Equal("{\"preset\":\"warm-ember\"}", File.ReadAllText(Path.Combine(_dir, "theme.json")));
    }

    [Fact]
    public void ApplySections_IgnoresUnknownSection_AndUsesCatalogFileName()
    {
        // A tampered profile claiming a rogue key / file name must NOT write outside the catalog.
        var sections = new[]
        {
            new ConfigSection("rogue", "Rogue", "..\\..\\evil.json", "should-not-write"),
            new ConfigSection("theme", "Theme", "theme.json", "{\"ok\":true}"),
        };
        var applied = _svc.ApplySections(sections);

        Assert.Equal(1, applied);                                   // only the known section
        Assert.False(File.Exists(Path.Combine(_dir, "evil.json"))); // rogue dropped
        Assert.True(File.Exists(Path.Combine(_dir, "theme.json")));
    }

    [Fact]
    public void Theme_UsesRoamingBase_Speedtest_UsesLocalBase()
    {
        // Regression: theme.json lives under Roaming AppData (ThemeService) while
        // speedtest-history.json lives under Local (SpeedTestHistoryService). The profiler
        // must read/write each from its OWN base, not a single shared dir.
        var local = Path.Combine(_dir, "Local");
        var roaming = Path.Combine(_dir, "Roaming");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(roaming);
        var svc = new ProfileService(local, roaming);

        // Apply both sections.
        svc.ApplySections(
        [
            new ConfigSection("theme", "Theme", "theme.json", "{\"preset\":\"midnight\"}"),
            new ConfigSection("speedtest", "Speed-test history", "speedtest-history.json", "[1,2]"),
        ]);

        // theme.json must land in Roaming; speedtest-history.json in Local.
        Assert.True(File.Exists(Path.Combine(roaming, "theme.json")));
        Assert.False(File.Exists(Path.Combine(local, "theme.json")));
        Assert.True(File.Exists(Path.Combine(local, "speedtest-history.json")));
        Assert.False(File.Exists(Path.Combine(roaming, "speedtest-history.json")));

        // And export reads them back from the correct bases.
        var sections = svc.AvailableSections();
        Assert.Contains(sections, s => s.Key == "theme" && s.Json.Contains("midnight"));
        Assert.Contains(sections, s => s.Key == "speedtest");
    }

    // ---------- Export / Import file round-trip ----------

    [Fact]
    public async Task ExportThenImport_File_RoundTrips()
    {
        WriteConfig("theme.json", "{\"preset\":\"violet-night\"}");
        var profilePath = Path.Combine(_dir, "exported-profile.json");

        await _svc.ExportToFileAsync(profilePath, _svc.BuildProfile(new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Local)));
        var imported = await _svc.ImportFromFileAsync(profilePath);

        Assert.NotNull(imported);
        Assert.Contains(imported!.Sections, s => s.Key == "theme" && s.Json.Contains("violet-night"));
    }
}
