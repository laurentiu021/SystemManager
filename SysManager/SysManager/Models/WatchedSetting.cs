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
    /// <summary>
    /// True when Settings Watchdog's Restore can write the baseline back: the setting is one it may write, and the
    /// baseline holds one of the values the setting is known to take. One that was not set when the baseline was saved
    /// cannot be put back, because the watchdog never deletes a value.
    /// </summary>
    public bool CanWriteBack => CanRestore && BaselineIsAKnownValue;

    /// <summary>
    /// True when the baseline holds one of the values <see cref="WatchedSetting.ValueLabels"/> lists for the setting.
    /// </summary>
    /// <remarks>
    /// The baseline is a file in the user's own profile, so anything running as the user can change the number in it,
    /// and Restore writes it, some of it to HKLM policy keys, as a DWORD (#2747). A value the catalog does not list is
    /// one SysManager never set, so it is not written back.
    /// </remarks>
    public bool BaselineIsAKnownValue => BaselineValue is { } value && Setting.ValueLabels.ContainsKey(value);

    public string BaselineLabel => Setting.Describe(BaselineValue);
    public string CurrentLabel => Setting.Describe(CurrentValue);
    public string Summary => $"{Setting.Name}: was \"{BaselineLabel}\", now \"{CurrentLabel}\"";
}
