// SysManager · SettingsWatchdogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class SettingsWatchdogServiceTests
{
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
        var svc = new SettingsWatchdogService();
        Assert.NotEmpty(svc.Catalog);
        var keys = svc.Catalog.Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Catalog_EverySettingHasNameAndRegistryPath()
    {
        var svc = new SettingsWatchdogService();
        Assert.All(svc.Catalog, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.RegistryPath));
            Assert.False(string.IsNullOrWhiteSpace(s.ValueName));
            // Only HKCU / HKLM hives are ever touched.
            Assert.Matches(@"^(HKCU|HKLM)\\", s.RegistryPath);
        });
    }
}
