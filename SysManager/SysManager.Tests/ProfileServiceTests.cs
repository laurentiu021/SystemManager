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

    // ---------- Unreadable config files ----------

    [Fact]
    public void AvailableSections_SkipsAFileItCannotRead_RatherThanThrowing()
    {
        // UnauthorizedAccessException is a SIBLING of IOException, not a subclass, so the
        // `catch (IOException)` around File.ReadAllText never covered an ACL-denied file — the
        // exception escaped and took the whole export with it, losing the sections that WERE
        // readable. A deny-read ACL is what actually produces it; a missing file is filtered out by
        // the File.Exists guard before the read, so it cannot exercise this path.
        WriteConfig("theme.json", "{\"preset\":\"midnight\"}");
        WriteConfig("speedtest-history.json", "[1,2,3]");

        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (sid is null) return;   // no SID to deny — nothing to assert on this host

        var denied = new FileInfo(Path.Combine(_dir, "theme.json"));
        var acl = denied.GetAccessControl();
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            sid,
            System.Security.AccessControl.FileSystemRights.Read,
            System.Security.AccessControl.AccessControlType.Deny);
        acl.AddAccessRule(rule);
        denied.SetAccessControl(acl);
        try
        {
            var sections = _svc.AvailableSections();

            // The readable one still comes through; the denied one is simply absent.
            Assert.Contains(sections, s => s.Key == "speedtest");
            Assert.DoesNotContain(sections, s => s.Key == "theme");
        }
        finally
        {
            // Remove the deny rule, or Dispose cannot delete the temp directory.
            acl.RemoveAccessRule(rule);
            denied.SetAccessControl(acl);
        }
    }

    // ---------- The catalog carries the user's choices, and nothing machine-specific ----------

    /// <summary>
    /// Every catalog section must actually surface once its file exists. A section whose file name does
    /// not match what the owning service writes exports NOTHING and fails silently — the profile just
    /// comes out smaller, which is exactly how six of these went missing for months.
    /// </summary>
    [Fact]
    public void AvailableSections_SurfacesEverySectionTheCatalogClaims()
    {
        // One file per section the catalog knows about, named as the owning services name them.
        string[] files =
        [
            "theme.json", "speedtest-history.json", "update-check.json", "darkmode-schedule.json",
            "gaming-profiles.json", "volume-presets.json", "close-preference.json",
            "standby-preference.json",
        ];
        foreach (var f in files) WriteConfig(f, "{}");

        var keys = _svc.AvailableSections().Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(files.Length, keys.Length);
        Assert.Equal(
            new[] { "closebehaviour", "darkmode", "gaming", "speedtest", "standby", "theme", "updatecheck", "volume" },
            keys);
    }

    /// <summary>
    /// Machine-specific state must never be carried to another PC. Each of these files is written by the
    /// app under the same config folder, so the ONLY thing keeping them out of a profile is their absence
    /// from the catalog — which is a one-line edit away from being undone by someone adding "everything".
    /// </summary>
    [Theory]
    [InlineData("performance-snapshot.json")]
    [InlineData("environment-backup.json")]
    [InlineData("settings-baseline.json")]
    [InlineData("service-startup-ledger.json")]
    [InlineData("last-crash.json")]
    [InlineData("activity.json")]
    [InlineData("resource-history-config.json")]
    [InlineData("disk-scan-history.json")]   // folder paths + sizes on THIS disk; meaningless on another PC
    public void AvailableSections_NeverCarriesMachineSpecificState(string fileName)
    {
        WriteConfig(fileName, "{\"machine\":\"specific\"}");

        Assert.Empty(_svc.AvailableSections());
    }

    /// <summary>
    /// A gaming profile carries the user's tick-boxes, but ActiveSession is the crash-recovery marker for
    /// a game running on the machine that exported it. Imported as-is, this PC would offer to restore
    /// tweaks it never applied for a game that never ran here.
    /// </summary>
    [Fact]
    public void ApplySections_GamingProfiles_KeepsTheProfilesAndDropsTheLiveSession()
    {
        var foreign = "{\"SchemaVersion\":1,\"LastConfig\":{\"FinestTimerResolution\":true},"
            + "\"ActiveSession\":{\"Profile\":{},\"Snapshot\":{}}}";

        var applied = _svc.ApplySections([new ConfigSection("gaming", "Gaming profiles", "gaming-profiles.json", foreign)]);

        Assert.Equal(1, applied);
        var written = File.ReadAllText(Path.Combine(_dir, "gaming-profiles.json"));
        Assert.DoesNotContain("ActiveSession", written, StringComparison.Ordinal);
        Assert.Contains("FinestTimerResolution", written, StringComparison.Ordinal);
        Assert.Contains("SchemaVersion", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable section is skipped, not written. Overwriting a working local file with content the
    /// owning service cannot parse would turn a bad import into lost settings.
    /// </summary>
    [Fact]
    public void ApplySections_GamingProfiles_UnparseableContentIsSkippedAndLeavesTheLocalFileAlone()
    {
        WriteConfig("gaming-profiles.json", "{\"SchemaVersion\":1,\"LastConfig\":{\"Mine\":true}}");

        var applied = _svc.ApplySections([new ConfigSection("gaming", "Gaming profiles", "gaming-profiles.json", "{ not json ][")]);

        Assert.Equal(0, applied);
        Assert.Contains("Mine", File.ReadAllText(Path.Combine(_dir, "gaming-profiles.json")),
            StringComparison.Ordinal);
    }
}
