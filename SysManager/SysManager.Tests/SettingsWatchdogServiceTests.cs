// SysManager · SettingsWatchdogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public sealed class SettingsWatchdogServiceTests : IDisposable
{
    private readonly string _dir;

    public SettingsWatchdogServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerWatchdogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone — nothing to clean up */ }
    }

    /// <summary>
    /// Always built against a TEMP directory. With the default, the service resolves
    /// <c>%LocalAppData%\SysManager\settings-baseline.json</c> — the user's real baseline — and
    /// SaveBaseline would overwrite it. Never construct this service in a test without a configDir.
    /// </summary>
    private SettingsWatchdogService NewService() => new(_dir);

    private string BaselineFile => Path.Combine(_dir, "settings-baseline.json");

    private static WatchedSetting Setting(string key, params (int, string)[] labels)
        => new(key, $"Name {key}", $"Desc {key}", "Cat", $@"HKCU\Software\Test\{key}", "Val",
            labels.ToDictionary(l => l.Item1, l => l.Item2));

    private static readonly IReadOnlyList<WatchedSetting> Catalog =
    [
        Setting("a", (0, "Off"), (1, "On")),
        Setting("b", (0, "Off"), (3, "Full")),
        Setting("c"),
    ];

    // ── DetectChanges ─────────────────────────────────────────────────────

    [Fact]
    public void DetectChanges_NullBaseline_ReturnsEmpty_DoesNotThrow()
    {
        // Regression: a baseline JSON file that parses but omits "Values" deserializes to a
        // BaselineSnapshot with Values == null. DetectChanges must treat that as "nothing
        // captured" rather than NRE-ing (which previously crashed the Refresh command).
        var ex = Record.Exception(() =>
            Assert.Empty(SettingsWatchdogService.DetectChanges(Catalog, null!, new Dictionary<string, int?>())));
        Assert.Null(ex);
    }

    [Fact]
    public void DetectChanges_NoDifference_ReturnsEmpty()
    {
        var baseline = new Dictionary<string, int?> { ["a"] = 0, ["b"] = 0, ["c"] = 1 };
        var current = new Dictionary<string, int?> { ["a"] = 0, ["b"] = 0, ["c"] = 1 };
        Assert.Empty(SettingsWatchdogService.DetectChanges(Catalog, baseline, current));
    }

    [Fact]
    public void DetectChanges_FlagsOnlyChangedSettings()
    {
        var baseline = new Dictionary<string, int?> { ["a"] = 0, ["b"] = 0, ["c"] = 1 };
        var current = new Dictionary<string, int?> { ["a"] = 1, ["b"] = 0, ["c"] = 1 };

        var drifts = SettingsWatchdogService.DetectChanges(Catalog, baseline, current);

        var drift = Assert.Single(drifts);
        Assert.Equal("a", drift.Setting.Key);
        Assert.Equal(0, drift.BaselineValue);
        Assert.Equal(1, drift.CurrentValue);
    }

    [Fact]
    public void DetectChanges_ValuePresentThenAbsent_IsDrift()
    {
        var baseline = new Dictionary<string, int?> { ["a"] = 1 };
        var current = new Dictionary<string, int?> { ["a"] = null };
        var drift = Assert.Single(SettingsWatchdogService.DetectChanges(Catalog, baseline, current));
        Assert.Equal(1, drift.BaselineValue);
        Assert.Null(drift.CurrentValue);
    }

    [Fact]
    public void DetectChanges_SettingNotInBaseline_IsSkipped()
    {
        // 'b' was never captured (absent from baseline) — even though current has a value,
        // it must not be reported (we have no baseline to compare/restore to).
        var baseline = new Dictionary<string, int?> { ["a"] = 0 };
        var current = new Dictionary<string, int?> { ["a"] = 0, ["b"] = 3 };
        Assert.Empty(SettingsWatchdogService.DetectChanges(Catalog, baseline, current));
    }

    [Fact]
    public void DetectChanges_PreservesCatalogOrder()
    {
        var baseline = new Dictionary<string, int?> { ["a"] = 0, ["b"] = 0 };
        var current = new Dictionary<string, int?> { ["a"] = 1, ["b"] = 3 };
        var drifts = SettingsWatchdogService.DetectChanges(Catalog, baseline, current);
        Assert.Equal(["a", "b"], drifts.Select(d => d.Setting.Key));
    }

    // ── Catalog contract ────────────────────────────────────────────────────

    [Fact]
    public void Catalog_IsNonEmpty_WithUniqueKeys()
    {
        var svc = NewService();
        Assert.NotEmpty(svc.Catalog);
        var keys = svc.Catalog.Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Catalog_EverySettingHasNameAndRegistryPath()
    {
        var svc = NewService();
        Assert.All(svc.Catalog, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.RegistryPath));
            Assert.False(string.IsNullOrWhiteSpace(s.ValueName));
            // Only HKCU / HKLM hives are ever touched.
            Assert.Matches(@"^(HKCU|HKLM)\\", s.RegistryPath);
        });
    }

    // ── Restore allowlist guard (idx 175) ────────────────────────────────────

    [Fact]
    public void Restore_OutOfCatalogSetting_IsRefused_AndWritesNothing()
    {
        // Regression (idx 175): Restore must only ever write a setting that is part of
        // its own curated catalog. A hand-built drift pointing at an arbitrary registry
        // path must be refused BEFORE any registry write — so this returns false without
        // ever touching the registry (the path below is never opened).
        var svc = NewService();
        var rogue = new WatchedSetting(
            "rogue", "Rogue", "Not in catalog", "Cat",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Evil",
            new Dictionary<int, string>());
        var drift = new SettingDrift(rogue, BaselineValue: 1, CurrentValue: 0, CanRestore: true);

        Assert.False(svc.Restore(drift));
    }

    [Fact]
    public void Restore_NonRestorableOrNoBaseline_ReturnsFalse()
    {
        var svc = NewService();
        var known = svc.Catalog[0];
        // CanRestore=false and null baseline are both early-out false paths.
        Assert.False(svc.Restore(new SettingDrift(known, BaselineValue: 1, CurrentValue: 0, CanRestore: false)));
        Assert.False(svc.Restore(new SettingDrift(known, BaselineValue: null, CurrentValue: 0, CanRestore: true)));
    }

    // ── a baseline value the catalog does not list is never written (#2747) ──

    private const string AdvertisingInfo = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";

    /// <summary>
    /// Runs <paramref name="act"/> against a throwaway HKCU root whose advertising ID value starts at 0, and returns
    /// what the value is afterwards. "advertising-id" is HKCU-rooted in the real catalog, as the hive-seam test below
    /// relies on too.
    /// </summary>
    private object? AdvertisingIdAfter(Action<SettingsWatchdogService, WatchedSetting> act)
    {
        var rootName = @"Software\SysManagerTests\WatchdogRestore_" + Guid.NewGuid().ToString("N");
        var root = Registry.CurrentUser.CreateSubKey(rootName, writable: true)!;
        try
        {
            using (var sub = root.CreateSubKey(AdvertisingInfo, writable: true)!)
                sub.SetValue("Enabled", 0, RegistryValueKind.DWord);
            var svc = new SettingsWatchdogService(_dir, hkcuRoot: root, hklmRoot: root);
            act(svc, svc.Catalog.Single(s => s.Key == "advertising-id"));
            using var check = root.OpenSubKey(AdvertisingInfo)!;
            return check.GetValue("Enabled");
        }
        finally
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(rootName, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// The baseline is a file in the user's own profile, so anything running as the user can change the number in it,
    /// and Restore wrote whatever it held as a DWORD, some of the catalog to HKLM policy keys. A value the catalog does
    /// not list for the setting is now refused, also when a hand-built drift carries labels that list it.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public void Restore_ABaselineValueTheCatalogDoesNotList_IsRefused_AndWritesNothing(int forged)
    {
        var after = AdvertisingIdAfter((svc, ads) =>
        {
            var permissive = ads with { ValueLabels = new Dictionary<int, string> { [forged] = "Forged" } };

            Assert.False(new SettingDrift(ads, BaselineValue: forged, CurrentValue: 0).CanWriteBack);
            Assert.False(svc.Restore(new SettingDrift(ads, BaselineValue: forged, CurrentValue: 0)));
            Assert.False(svc.Restore(new SettingDrift(permissive, BaselineValue: forged, CurrentValue: 0)));
        });

        Assert.Equal(0, after);
    }

    /// <summary>The other side: a value the catalog lists is still written, so the check is not an off-switch.</summary>
    [Fact]
    public void Restore_ABaselineValueTheCatalogLists_IsWritten()
    {
        var after = AdvertisingIdAfter((svc, ads) =>
            Assert.True(svc.Restore(new SettingDrift(ads, BaselineValue: 1, CurrentValue: 0))));

        Assert.Equal(1, after);
    }

    // ── hive seam (#2860) ───────────────────────────────────────────────────────────────────────
    //
    // Before this, ReadValue and OpenOrCreateKey were private static, hard-wired to Registry.CurrentUser
    // and Registry.LocalMachine, so no test could prove the read went through an injected root at all —
    // every "read" test here was reading this machine's real policies and happened to pass because this
    // machine's values matched the test's expectations. The hive seam shares RegistryPaths.OpenOrCreateKey
    // with PrivacyService without changing the Watchdog's default root (still Registry.CurrentUser, not
    // Privacy's AdminHelper.PerUserRegistryRoot() — the two defaults are reconciled by #2826, not here).

    [Fact]
    public void ReadCurrent_UsesTheInjectedHive_NotTheRealOne()
    {
        var rootName = @"Software\SysManagerTests\WatchdogHive_" + Guid.NewGuid().ToString("N");
        var root = Registry.CurrentUser.CreateSubKey(rootName, writable: true)!;
        try
        {
            // "advertising-id" is HKCU-rooted in the real catalog; writing it under the throwaway root
            // and reading it back through the service proves the injected hkcuRoot reached the read,
            // not Registry.CurrentUser itself.
            using (var sub = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", writable: true)!)
                sub.SetValue("Enabled", 1, RegistryValueKind.DWord);

            var svc = new SettingsWatchdogService(_dir, hkcuRoot: root, hklmRoot: root);
            var current = svc.ReadCurrent();

            Assert.Equal(1, current["advertising-id"]);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootName, throwOnMissingSubKey: false);
        }
    }

    // ── configDir seam ───────────────────────────────────────────────────────────────────────
    //
    // The baseline path used to be `static readonly` under %LocalAppData%. Environment.GetFolderPath
    // resolves through the Win32 known-folder API and ignores the LOCALAPPDATA environment variable,
    // so no test could redirect it — meaning any test calling SaveBaseline would overwrite the user's
    // own saved baseline. Realised twice already in this codebase: SpeedTestHistoryService's tests
    // deleted the user's speed-test history, and AppIconService's overwrote a real setting on every
    // run (#1758). Tracked by ArchitectureTests.Services_DoNotHoldUserDataPathsInStaticFields.

    [Fact]
    public void SaveBaseline_WritesInsideTheGivenConfigDir()
    {
        Assert.False(File.Exists(BaselineFile));

        NewService().SaveBaseline(new DateTime(2026, 3, 1, 12, 0, 0));

        Assert.True(File.Exists(BaselineFile));
    }

    [Fact]
    public void SaveBaseline_WhenTheFileCannotBeWritten_Throws_InsteadOfSwallowingIt()
    {
        // A folder where the baseline file belongs makes the atomic write fail, as a locked or unwritable file
        // does. The failure used to be caught and logged at Debug level, so the tab said "Baseline saved" (#2452).
        Directory.CreateDirectory(BaselineFile);

        var ex = Record.Exception(() => NewService().SaveBaseline(new DateTime(2026, 3, 1, 12, 0, 0)));

        Assert.True(ex is IOException or UnauthorizedAccessException,
            $"expected a write failure, got {ex?.GetType().Name ?? "no exception"}");
    }

    [Fact]
    public void BaselineFileExists_ReflectsTheGivenConfigDir()
    {
        var svc = NewService();
        Assert.False(svc.BaselineFileExists);

        svc.SaveBaseline(new DateTime(2026, 3, 1, 12, 0, 0));

        Assert.True(NewService().BaselineFileExists);
    }

    [Fact]
    public void LoadBaseline_RoundTripsWhatWasSaved()
    {
        // Persistence is the feature — the whole point of a baseline is that it survives a restart.
        var takenAt = new DateTime(2026, 3, 1, 12, 0, 0);   // fixed: never DateTime.Now
        var saved = NewService().SaveBaseline(takenAt);

        var loaded = NewService().LoadBaseline();

        Assert.NotNull(loaded);
        Assert.Equal(takenAt, loaded!.TakenAt);
        Assert.Equal(saved.Count, loaded.Values.Count);
    }

    [Fact]
    public void TwoServicesWithDifferentConfigDirs_DoNotShareABaseline()
    {
        // Proves the path is genuinely per-instance. If it were still static, the second service would
        // see the first one's baseline — which is exactly the coupling that let a test reach the real
        // profile.
        var otherDir = Path.Combine(Path.GetTempPath(), "SysManagerWatchdogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherDir);
        try
        {
            NewService().SaveBaseline(new DateTime(2026, 3, 1, 12, 0, 0));

            Assert.False(new SettingsWatchdogService(otherDir).BaselineFileExists);
            Assert.True(NewService().BaselineFileExists);
        }
        finally { Directory.Delete(otherDir, recursive: true); }
    }

    // A baseline that is there and did not load read as "none", so a save replaced it without asking (#2521).

    [Fact]
    public void SaveBaseline_OverAFileThatDoesNotParse_KeepsItAside_ThenSaves()
    {
        File.WriteAllText(BaselineFile, "{ this is not json");

        NewService().SaveBaseline(new DateTime(2026, 3, 1, 12, 0, 0));

        Assert.Equal("{ this is not json", File.ReadAllText(BaselineFile + ".unreadable"));
        Assert.NotNull(NewService().LoadBaseline());
    }

    [Fact]
    public void SaveBaseline_WhenABaselineThatDoesNotLoadCannotBeSetAside_Throws_AndLeavesIt()
    {
        File.WriteAllText(BaselineFile, "{ this is not json");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not,
        // so only the refusal keeps the file. A sharing hold would fail the write as well and prove nothing.
        Directory.CreateDirectory(BaselineFile + ".unreadable");

        Assert.Throws<IOException>(() => NewService().SaveBaseline(new DateTime(2026, 3, 1, 12, 0, 0)));

        Assert.Equal("{ this is not json", File.ReadAllText(BaselineFile));
    }

    [Fact]
    public void LoadBaseline_WhenTheFileCannotBeRead_ReturnsNull_ButTheFileStillCounts()
    {
        NewService().SaveBaseline(new DateTime(2026, 3, 1, 12, 0, 0));

        using (new FileStream(BaselineFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            var svc = NewService();
            Assert.Null(svc.LoadBaseline());
            Assert.True(svc.BaselineFileExists);
        }
    }

    [Fact]
    public void LoadBaseline_MalformedFile_ReturnsNull_RatherThanThrowing()
    {
        // Persisted state is a trust boundary: a corrupt file must degrade to "no baseline", not crash
        // the tab.
        File.WriteAllText(BaselineFile, "{ this is not json");

        Assert.Null(NewService().LoadBaseline());
    }
}
