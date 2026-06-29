// SysManager · WatchedSetting — a Windows setting the Settings Watchdog snapshots and monitors
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Definition of a single registry-backed Windows setting the watchdog tracks.
/// Settings here are ones Windows Update (or a feature update) commonly resets —
/// telemetry level, web search, widgets, lock-screen ads, and so on. The watchdog
/// snapshots the user's current value as a baseline and later flags any drift.
/// </summary>
public sealed record WatchedSetting(
    string Key,
    string Name,
    string Description,
    string Category,
    string RegistryPath,
    string ValueName,
    // Human-readable interpretation of a raw value (e.g. 0 → "Off", 3 → "Full"), so the
    // before/after comparison reads in plain language rather than as bare numbers.
    IReadOnlyDictionary<int, string> ValueLabels)
{
    /// <summary>Renders a raw value in plain language, falling back to the number when unmapped or absent.</summary>
    public string Describe(int? value) =>
        value is null ? "Not set"
        : ValueLabels.TryGetValue(value.Value, out var label) ? label
        : value.Value.ToString();
}

/// <summary>
/// One detected difference between the saved baseline and the current system state.
/// CanRestore is false for read-only watched values (e.g. default browser), which are
/// surfaced for awareness but cannot be written back safely.
/// </summary>
public sealed record SettingDrift(
    WatchedSetting Setting,
    int? BaselineValue,
    int? CurrentValue,
    bool CanRestore = true)
{
    public string BaselineLabel => Setting.Describe(BaselineValue);
    public string CurrentLabel => Setting.Describe(CurrentValue);
    public string Summary => $"{Setting.Name}: was \"{BaselineLabel}\", now \"{CurrentLabel}\"";
}
