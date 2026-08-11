// SysManager · SettingsWatchdogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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
    public void HasBaseline_ReflectsTheGivenConfigDir()
    {
        var svc = NewService();
        Assert.False(svc.HasBaseline);

        svc.SaveBaseline(new DateTime(2026, 3, 1, 12, 0, 0));

        Assert.True(NewService().HasBaseline);
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

            Assert.False(new SettingsWatchdogService(otherDir).HasBaseline);
            Assert.True(NewService().HasBaseline);
        }
        finally { Directory.Delete(otherDir, recursive: true); }
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
