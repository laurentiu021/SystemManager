// SysManager · SettingsWatchdogService — snapshots and monitors settings Windows Update tends to reset
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Watches a curated set of Windows settings that feature/quality updates frequently
/// reset (telemetry level, web search, widgets, lock-screen ads, …). The user saves a
/// baseline snapshot of their preferences; later the watchdog re-reads the live values
/// and reports any drift in plain language, with one-click restore of the registry-backed
/// settings. Strictly local — reads/writes only well-known registry values, nothing leaves
/// the machine. Registry access mirrors <see cref="PrivacyService"/>'s validated helper.
/// </summary>
public sealed class SettingsWatchdogService : ISettingsWatchdogService
{
    private static readonly string BaselinePath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysManager", "settings-baseline.json");

    /// <summary>The catalog of settings the watchdog tracks. Stable order for the UI.</summary>
    public IReadOnlyList<WatchedSetting> Catalog { get; } = BuildCatalog();

    /// <summary>Reads the current raw value of each watched setting (null if absent).</summary>
    public IReadOnlyDictionary<string, int?> ReadCurrent()
    {
        var map = new Dictionary<string, int?>(Catalog.Count);
        foreach (var s in Catalog)
            map[s.Key] = ReadValue(s);
        return map;
    }

    /// <summary>Captures the current values as the saved baseline. Returns the snapshot taken.</summary>
    public IReadOnlyDictionary<string, int?> SaveBaseline(DateTime takenAt)
    {
        var current = ReadCurrent();
        Persist(new BaselineSnapshot(takenAt, new Dictionary<string, int?>(current)));
        return current;
    }

    /// <summary>Loads the saved baseline, or null if none exists yet.</summary>
    public BaselineSnapshot? LoadBaseline()
    {
        try
        {
            if (!File.Exists(BaselinePath)) return null;
            var snapshot = JsonSerializer.Deserialize<BaselineSnapshot>(File.ReadAllText(BaselinePath));
            // A baseline file that parses as JSON but omits the "Values" property deserializes
            // with Values == null (System.Text.Json does not enforce non-null on positional
            // record params). Normalize it to an empty map so downstream diffing never NREs on
            // a malformed-but-parseable file.
            return snapshot is { Values: null } ? snapshot with { Values = [] } : snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Debug("Settings baseline load failed: {Error}", ex.Message);
            return null;
        }
    }

    public bool HasBaseline => File.Exists(BaselinePath);

    /// <summary>
    /// Compares the saved baseline against the live system and returns the drifted settings.
    /// Returns an empty list when there is no baseline or nothing changed.
    /// </summary>
    public IReadOnlyList<SettingDrift> DetectDrift()
    {
        var baseline = LoadBaseline();
        if (baseline is null) return [];
        return DetectChanges(Catalog, baseline.Values, ReadCurrent());
    }

    /// <summary>
    /// Writes the baseline value back for a single drifted setting. Returns true on success,
    /// false if the value can't be restored (read-only, no baseline value, or write denied).
    /// </summary>
    public bool Restore(SettingDrift drift)
    {
        ArgumentNullException.ThrowIfNull(drift);
        if (!drift.CanRestore || drift.BaselineValue is null) return false;

        // Allowlist guard: only ever write a setting that is part of our own curated
        // catalog (matched by exact hive+path AND value name). The catalog is the single
        // source of truth, so the writer can never be repurposed for an arbitrary
        // registry path even if a caller hands us a hand-built drift.
        var inCatalog = Catalog.Any(s =>
            string.Equals(s.RegistryPath, drift.Setting.RegistryPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.ValueName, drift.Setting.ValueName, StringComparison.OrdinalIgnoreCase));
        if (!inCatalog)
        {
            Log.Warning("Settings Watchdog refused to restore an out-of-catalog setting: {Path}\\{Value}",
                drift.Setting.RegistryPath, drift.Setting.ValueName);
            return false;
        }

        try
        {
            using var key = OpenOrCreateKey(drift.Setting.RegistryPath, writable: true);
            if (key is null) return false;
            key.SetValue(drift.Setting.ValueName, drift.BaselineValue.Value, RegistryValueKind.DWord);
            Log.Information("Settings Watchdog restored {Name} to {Value}",
                drift.Setting.Name, drift.BaselineValue.Value);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException)
        {
            Log.Warning(ex, "Settings Watchdog could not restore {Name}", drift.Setting.Name);
            return false;
        }
    }

    // ── Pure diff (unit-testable, no registry/file IO) ─────────────────────

    /// <summary>
    /// Returns the settings whose current value differs from the baseline. A setting absent
    /// from the baseline map is skipped (it was never captured). Pure and order-preserving.
    /// </summary>
    public static IReadOnlyList<SettingDrift> DetectChanges(
        IReadOnlyList<WatchedSetting> catalog,
        IReadOnlyDictionary<string, int?> baseline,
        IReadOnlyDictionary<string, int?> current)
    {
        // Defensive: a malformed/legacy baseline can yield a null map. Treat it as "nothing
        // captured" rather than throwing — this is the trust boundary for persisted state.
        if (baseline is null) return [];
        var drifts = new List<SettingDrift>();
        foreach (var setting in catalog)
        {
            if (!baseline.TryGetValue(setting.Key, out var baseValue)) continue;
            current.TryGetValue(setting.Key, out var curValue);
            if (baseValue != curValue)
                drifts.Add(new SettingDrift(setting, baseValue, curValue));
        }
        return drifts;
    }

    // ── Registry access (mirrors PrivacyService) ───────────────────────────

    private static int? ReadValue(WatchedSetting setting)
    {
        try
        {
            using var key = OpenOrCreateKey(setting.RegistryPath, writable: false);
            var value = key?.GetValue(setting.ValueName);
            if (value is int i) return i;
            if (value is not null && int.TryParse(value.ToString(), out var parsed)) return parsed;
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            Log.Debug("Settings Watchdog cannot read {Path}\\{Value}: {Error}",
                setting.RegistryPath, setting.ValueName, ex.Message);
            return null;
        }
    }

    private static RegistryKey? OpenOrCreateKey(string fullPath, bool writable)
    {
        var sep = fullPath.IndexOf('\\');
        if (sep < 0) return null;
        var hiveName = fullPath[..sep].ToUpperInvariant();
        var subPath = fullPath[(sep + 1)..];
        var hive = hiveName switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            _ => null
        };
        if (hive is null) return null;
        return writable ? hive.CreateSubKey(subPath, writable: true) : hive.OpenSubKey(subPath, writable: false);
    }

    private static void Persist(BaselineSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
            File.WriteAllText(BaselinePath, JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex) { Log.Debug("Settings baseline save failed: {Error}", ex.Message); }
    }

    // ── Catalog ─────────────────────────────────────────────────────────────

    private static List<WatchedSetting> BuildCatalog()
    {
        var onOff = new Dictionary<int, string> { [0] = "Off", [1] = "On" };
        var offOn = new Dictionary<int, string> { [0] = "Enabled", [1] = "Disabled" };

        return
        [
            new WatchedSetting("telemetry", "Diagnostic data (telemetry)",
                "Windows Update can raise the telemetry level back to Full.",
                "Privacy", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry",
                new Dictionary<int, string> { [0] = "Off (Security)", [1] = "Required", [2] = "Enhanced", [3] = "Full" }),

            new WatchedSetting("advertising-id", "Advertising ID",
                "Targeted-ad ID that updates sometimes re-enable.",
                "Privacy", @"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", onOff),

            new WatchedSetting("activity-feed", "Activity history",
                "Collection of your activity timeline.",
                "Privacy", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", onOff),

            new WatchedSetting("web-search", "Web search in Start",
                "Bing web results in the Start-menu search box.",
                "Search", @"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", offOn),

            new WatchedSetting("widgets", "Widgets (news & interests)",
                "The taskbar Widgets board, often re-enabled by updates.",
                "Taskbar", @"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", onOff),

            new WatchedSetting("start-suggestions", "Start menu suggestions",
                "Suggested apps and promoted content in Start.",
                "UI Declutter", @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SubscribedContent-338388Enabled", onOff),

            new WatchedSetting("lockscreen-ads", "Lock-screen tips & ads",
                "Spotlight tips and promotions on the lock screen.",
                "UI Declutter", @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "RotatingLockScreenOverlayEnabled", onOff),

            new WatchedSetting("tips", "Windows tips",
                "Tips-and-suggestions notifications.",
                "UI Declutter", @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SoftLandingEnabled", onOff),
        ];
    }
}

/// <summary>The saved baseline of watched setting values, with when it was captured.</summary>
public sealed record BaselineSnapshot(DateTime TakenAt, Dictionary<string, int?> Values);
