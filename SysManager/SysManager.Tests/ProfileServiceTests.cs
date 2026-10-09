// SysManager · ProfileServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NSubstitute;
using SysManager.Helpers;
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
        _svc = new ProfileService(FakePrivacy.AllOff(), _dir);
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
        // Config files alone are written as version 1, the format every build since profiles shipped reads.
        Assert.Equal(1, restored!.SchemaVersion);
        Assert.Equal(2, restored.Sections.Count);
        Assert.Contains(restored.Sections, s => s.Key == "theme" && s.Json.Contains("deep-ocean"));
    }

    // ---------- BuildProfile reads the files when it is called (#2477) ----------

    [Fact]
    public void BuildProfile_ReadsEachFileAsItIsWhenCalled()
    {
        // Two exports from the same service, as the tab makes over a session: the second writes the file as it
        // is then, not as the first one found it.
        var at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        WriteConfig("theme.json", "{\"preset\":\"before\"}");
        Assert.Contains("before", Assert.Single(_svc.BuildProfile(at, ["theme"]).Sections).Json, StringComparison.Ordinal);

        WriteConfig("theme.json", "{\"preset\":\"after\"}");
        var profile = _svc.BuildProfile(at, ["theme"]);

        Assert.Contains("after", Assert.Single(profile.Sections).Json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProfile_WithKeys_CarriesOnlyThoseSections()
    {
        WriteConfig("theme.json", "{\"preset\":\"midnight\"}");
        WriteConfig("volume-presets.json", "[]");

        var profile = _svc.BuildProfile(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local), ["volume"]);

        Assert.Equal(["volume"], profile.Sections.Select(s => s.Key));
    }

    [Fact]
    public void BuildProfile_LeavesOutAKeyWhoseFileDoesNotExist()
    {
        WriteConfig("theme.json", "{\"preset\":\"midnight\"}");

        var profile = _svc.BuildProfile(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local), ["theme", "volume"]);

        Assert.Equal(["theme"], profile.Sections.Select(s => s.Key));
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
        var svc = new ProfileService(FakePrivacy.AllOff(), local, roaming);

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
        // Asserted rather than returned on. The property is nullable only because an anonymous token has no
        // user SID, and a process token is never anonymous — so the check was there to satisfy nullable
        // analysis, which Assert.NotNull does just as well while making the impossible case loud instead of
        // reporting a pass that denied nothing.
        Assert.NotNull(sid);

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
            "standby-preference.json", "icon-fetch.json",
        ];
        foreach (var f in files) WriteConfig(f, "{}");

        var keys = _svc.AvailableSections().Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(files.Length, keys.Length);
        Assert.Equal(
            new[] { "appicons", "closebehaviour", "darkmode", "gaming", "speedtest", "standby", "theme",
                    "updatecheck", "volume" },
            keys);
    }

    /// <summary>
    /// No two catalog entries may share a Key or a FileName.
    /// </summary>
    /// <remarks>
    /// Two separate things depend on the Key being unique, and neither says so. <c>ApplySections</c> resolves
    /// an imported section with <c>Array.Find(Catalog, c =&gt; c.Key == section.Key)</c>, so a duplicate Key
    /// silently skips the second entry's <c>OnImport</c> sanitiser — the thing that strips machine-specific
    /// state out of a foreign profile. And <c>ProfileViewModel</c> carries the user's ticks across a refresh
    /// on <c>s.Section.Key</c>, where a duplicate means one section's tick becomes another's (#2405). A
    /// duplicate FileName is the same defect from the other end: two sections writing one file on import.
    /// <para>Read off the catalog itself rather than through <c>AvailableSections()</c>, which only surfaces
    /// a section whose file exists — so a duplicate Key on an entry whose file the test did not plant would
    /// never appear. The sibling test above catches only the case where the two share a file name.</para>
    /// </remarks>
    [Fact]
    public void TheCatalog_HasNoDuplicateKeyOrFileName()
    {
        var field = typeof(ProfileService).GetField("Catalog", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field is not null,
            "ProfileService has no Catalog field. If the section list moved, point this guard at it — the "
            + "uniqueness of a section key is load-bearing for import sanitising and for tick carry-over.");

        var entries = field!.GetValue(null) as Array;
        Assert.True(entries is { Length: > 0 }, "The catalog is empty, so this guard would measure nothing.");

        // Read positionally through ITuple: the tuple carries a private enum, so its type is not nameable
        // here, and the field order (Key, DisplayName, FileName, …) is what ProfileService itself destructures.
        var keys = new List<string>(entries!.Length);
        var fileNames = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            var tuple = Assert.IsAssignableFrom<ITuple>(entry);
            keys.Add(Assert.IsType<string>(tuple[0]));
            fileNames.Add(Assert.IsType<string>(tuple[2]));
        }

        // The privacy section lives outside the catalog but shares its key and file-name space: a catalog entry
        // keyed "privacy" would be handed to the Privacy & Telemetry tab instead of written. Its real values,
        // read off a built section rather than restated here.
        var privacy = Assert.Single(new ProfileService(FakePrivacy.Returning(FakePrivacy.Toggle("tips", on: true)), _dir)
            .AvailableSections());
        keys.Add(privacy.Key);
        fileNames.Add(privacy.FileName);

        // Through the same helper the carry itself uses, and the same comparer ProfileViewModel passes.
        Assert.Empty(SelectionCarry.DuplicateKeys(keys, k => k, StringComparer.Ordinal));

        // Case-insensitive for the file names: the config folder is NTFS, so two entries differing only in
        // case are one file, and an import would write both sections over each other.
        Assert.Empty(SelectionCarry.DuplicateKeys(fileNames, f => f, StringComparer.OrdinalIgnoreCase));

        // The floor. Nine sections ship today; a guard that passed over a catalog of one would prove nothing.
        Assert.True(keys.Count >= 9, $"Only {keys.Count} catalog entries read, so uniqueness proves little.");
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
    [InlineData("disk-analyzer-preference.json")]   // one yes or no for the map; not worth a section of its own
    [InlineData("disk-scan-history.json")]   // folder paths + sizes on THIS disk; meaningless on another PC
    // A count of how many times the main toggle was written on THIS machine, used to decide whether a
    // restore point is still needed. Carried over, it would claim work had been done here that was not.
    [InlineData("notification-master-writes.json")]
    // Folders an uninstall left under Program Files or ProgramData on THIS machine, waiting for administrator rights.
    // Carried over, another PC would be offered folders of programs it never had (#1527).
    [InlineData("uninstaller-leftovers.json")]
    // What Recent Changes compares from one look to the next, and what New App Alerts and Settings Watchdog noticed
    // on THIS machine (#1507). Carried over, another PC would list programs and settings it never had as changes.
    [InlineData("installed-programs.json")]
    [InlineData("app-alerts.json")]
    [InlineData("settings-drift.json")]
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

    // ---------- A profile file someone else made (the import's trust boundary) ----------

    /// <summary>
    /// A section with no key or no content, or a null in the list, is dropped when the file is read.
    /// </summary>
    /// <remarks>
    /// Each used to get further than it should and then throw something the import does not catch. A null in
    /// the list threw while the confirmation was being built from the sections' names. A known key with no
    /// content was confirmed and then handed to the atomic write, which refuses null. Either took the tab down
    /// over a bad file instead of saying so. Present since v1.28.0, when Profile Export/Import was added.
    /// </remarks>
    [Fact]
    public void Deserialize_DropsASectionItCannotUse()
    {
        var json = $"{{\"SchemaVersion\":1,\"AppVersion\":\"1.0.0\",\"ExportedAt\":\"2026-01-01T00:00:00\",\"Sections\":["
            + "null,"
            + "{\"DisplayName\":\"No key\",\"FileName\":\"theme.json\",\"Json\":\"{}\"},"
            + "{\"Key\":\"theme\",\"DisplayName\":\"No content\",\"FileName\":\"theme.json\"},"
            + "{\"Key\":\"volume\",\"DisplayName\":\"Volume presets\",\"FileName\":\"volume-presets.json\",\"Json\":\"[]\"}"
            + "]}";

        var profile = ProfileService.Deserialize(json);

        Assert.NotNull(profile);
        var section = Assert.Single(profile!.Sections);
        Assert.Equal("volume", section.Key);
    }

    // ---------- The privacy section (#1530) ----------
    // The one section that is not a file: the Privacy & Telemetry toggles, read from the registry when the
    // profile is built and never written by an import. These run over FakePrivacy, so the answer does not
    // depend on the privacy settings of the machine running the suite.

    private static readonly DateTime ExportedAt = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Local);

    private ProfileService WithPrivacy(params PrivacyToggle[] toggles) => new(FakePrivacy.Returning(toggles), _dir);

    [Fact]
    public void AvailableSections_ListsThePrivacyChoices_OnlyOnceAProtectionIsOn()
    {
        // Every protection off is Windows' own default, so there is nothing to carry.
        Assert.DoesNotContain(_svc.AvailableSections(), ProfileService.IsPrivacySection);

        var svc = WithPrivacy(FakePrivacy.Toggle("tips", on: true), FakePrivacy.Toggle("widgets", on: false));

        var privacy = Assert.Single(svc.AvailableSections());
        Assert.Equal(ProfileService.PrivacySectionKey, privacy.Key);
        Assert.Equal("Privacy & Telemetry choices", privacy.DisplayName);
    }

    [Fact]
    public void ThePrivacySection_CarriesEveryToggle_OnAndOff()
    {
        // The whole posture: an off switch is as much a choice to carry as an on one.
        var svc = WithPrivacy(FakePrivacy.Toggle("tips", on: true), FakePrivacy.Toggle("widgets", on: false));
        var section = Assert.Single(svc.BuildProfile(ExportedAt).Sections);

        var choices = svc.ReadPrivacyChoices(section);

        Assert.NotNull(choices);
        Assert.Equal([("tips", true), ("widgets", false)],
            choices!.Protections.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => (c.Key, c.Value)));
    }

    [Fact]
    public void BuildProfile_ReadsThePrivacyTogglesAsTheyAreWhenCalled()
    {
        // The same rule as the files (#2477): what is exported is what this PC has now, not what it had when
        // the tab opened.
        var tipsOn = true;
        var privacy = Substitute.For<IPrivacyService>();
        privacy.LoadToggles().Returns(_ => [FakePrivacy.Toggle("tips", tipsOn), FakePrivacy.Toggle("widgets", on: true)]);
        var svc = new ProfileService(privacy, _dir);
        Assert.Single(svc.BuildProfile(ExportedAt).Sections);

        tipsOn = false;
        var section = Assert.Single(svc.BuildProfile(ExportedAt).Sections);

        Assert.False(svc.ReadPrivacyChoices(section)!.Protections["tips"]);
    }

    [Fact]
    public void BuildProfile_WritesVersionTwoOnlyWhenItCarriesPrivacyChoices()
    {
        WriteConfig("theme.json", "{\"preset\":\"midnight\"}");
        var svc = WithPrivacy(FakePrivacy.Toggle("tips", on: true));

        // Version 2 is what makes an older SysManager refuse the profile with "update SysManager" rather than
        // import its files and silently leave the privacy choices behind.
        Assert.Equal(ProfileService.CurrentSchemaVersion, svc.BuildProfile(ExportedAt).SchemaVersion);
        Assert.Equal(2, ProfileService.CurrentSchemaVersion);

        // Without them the profile is one every build can import, so it says version 1.
        Assert.Equal(1, svc.BuildProfile(ExportedAt, ["theme"]).SchemaVersion);
    }

    [Fact]
    public void Deserialize_AVersionOneProfile_StillImports()
    {
        // Every profile exported before the privacy section existed says version 1.
        var json = "{\"SchemaVersion\":1,\"AppVersion\":\"1.114.54\",\"ExportedAt\":\"2026-10-01T00:00:00\","
            + "\"Sections\":[{\"Key\":\"theme\",\"DisplayName\":\"Theme & appearance\",\"FileName\":\"theme.json\","
            + "\"Json\":\"{}\"}]}";

        var profile = ProfileService.Deserialize(json);

        Assert.Equal("theme", Assert.Single(profile!.Sections).Key);
    }

    [Fact]
    public void ApplySections_NeverWritesThePrivacySection()
    {
        // Import hands the privacy choices to the Privacy & Telemetry tab. Even passed here, they are not a file.
        var section = Assert.Single(WithPrivacy(FakePrivacy.Toggle("tips", on: true)).AvailableSections());

        var applied = _svc.ApplySections([section]);

        Assert.Equal(0, applied);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ReadPrivacyChoices_KeepsOnlyTheTogglesThisBuildKnows()
    {
        var svc = WithPrivacy(FakePrivacy.Toggle("tips", on: false), FakePrivacy.Toggle("widgets", on: false));
        var section = new ConfigSection(ProfileService.PrivacySectionKey, "Privacy & Telemetry choices",
            "privacy-profile.json", "{\"Protections\":{\"tips\":true,\"a-toggle-from-a-newer-build\":true}}");

        var choices = svc.ReadPrivacyChoices(section);

        Assert.NotNull(choices);
        Assert.Equal(["tips"], choices!.Protections.Keys);
        Assert.True(choices.Protections["tips"]);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Protections\":null}")]
    [InlineData("{\"Protections\":{}}")]
    [InlineData("{\"Protections\":{\"tips\":\"yes\"}}")]
    [InlineData("{\"Protections\":{\"tips\":null}}")]
    [InlineData("{\"Protections\":{\"only-unknown-toggles\":true}}")]
    public void ReadPrivacyChoices_RefusesContentItCannotUse(string json)
    {
        var svc = WithPrivacy(FakePrivacy.Toggle("tips", on: false));
        var section = new ConfigSection(ProfileService.PrivacySectionKey, "Privacy & Telemetry choices",
            "privacy-profile.json", json);

        Assert.Null(svc.ReadPrivacyChoices(section));
    }

    [Fact]
    public void ReadPrivacyChoices_ARepeatedToggle_IsReadRatherThanThrown()
    {
        // A hand-edited file can name a toggle twice. Whatever the parser makes of it, the import must not throw
        // something it does not catch.
        var svc = WithPrivacy(FakePrivacy.Toggle("tips", on: false));
        var section = new ConfigSection(ProfileService.PrivacySectionKey, "Privacy & Telemetry choices",
            "privacy-profile.json", "{\"Protections\":{\"tips\":true,\"tips\":false}}");

        var choices = svc.ReadPrivacyChoices(section);

        Assert.NotNull(choices);
        Assert.Equal(["tips"], choices!.Protections.Keys);
    }
}
