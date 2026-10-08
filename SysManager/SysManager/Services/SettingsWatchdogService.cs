// SysManager · SettingsWatchdogService — snapshots and monitors settings Windows Update tends to reset
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
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
    // Instance, not static readonly. Environment.GetFolderPath resolves through the Win32
    // known-folder API and ignores the LOCALAPPDATA environment variable, so a static path cannot be
    // redirected by any test — not even one in a child process. SpeedTestHistoryService held its path
    // that way and its tests deleted the user's real speed-test history; AppIconService did the same
    // and its tests overwrote a real setting on every run (#1758). See
    // ArchitectureTests.Services_DoNotHoldUserDataPathsInStaticFields.
    private readonly string _baselinePath;

    // When each drift was first seen, beside the baseline it was measured against (#1507).
    private readonly string _sightingsPath;

    /// <summary>
    /// How many sightings are kept. A setting two programs keep flipping adds one at every look; the open ones, one a
    /// setting at most, are always kept, and past this the ones that ended longest ago go.
    /// </summary>
    internal const int MaxSightings = 200;

    // Serializes SaveBaseline, which reads the baseline file to decide whether to set it aside before replacing it, and
    // RecordDrift, which reads the sightings and writes them back: saving a baseline forgets the sightings, and a look
    // in between would otherwise write them back.
    private readonly Lock _saveLock = new();

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="configDir">
    /// Where the baseline snapshot lives. Null uses <c>%LocalAppData%\SysManager</c>, which is
    /// correct in production; tests MUST pass a temp directory, or they read and write the user's
    /// real baseline.
    /// </param>
    public SettingsWatchdogService(string? configDir = null)
    {
        var dir = configDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _baselinePath = Path.Join(dir, "settings-baseline.json");
        _sightingsPath = Path.Join(dir, "settings-drift.json");
    }

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
    /// <remarks>
    /// A baseline file that is there and did not load is set aside first, so its bytes survive (#2521).
    /// </remarks>
    /// <exception cref="IOException">
    /// The baseline file could not be written, or one that did not load could not be set aside.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The baseline folder or file is not writable.</exception>
    public IReadOnlyDictionary<string, int?> SaveBaseline(DateTime takenAt)
    {
        // The check and the write are one step: the check reads the file this write replaces.
        lock (_saveLock)
        {
            if (BaselineFileExists && LoadBaseline() is null && !StoreFile.SetAside(_baselinePath))
                throw new IOException("The saved baseline could not be read or moved aside, so it was not replaced.");

            var current = ReadCurrent();
            Persist(new BaselineSnapshot(takenAt, new Dictionary<string, int?>(current)));
            // Measured against the baseline just replaced, so they no longer mean anything.
            ForgetSightings();
            return current;
        }
    }

    /// <inheritdoc />
    public DriftSightings RecordDrift(IReadOnlyList<SettingDrift> drifts, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(drifts);
        lock (_saveLock)
        {
            var json = StoreFile.ReadText(_sightingsPath);
            if (json is null) return DriftSightings.Unreadable;

            var kept = json.Length == 0 ? [] : ParseSightings(json);
            if (kept is null)
            {
                if (!StoreFile.SetAside(_sightingsPath)) return DriftSightings.Unreadable;
                kept = [];
            }

            var sightings = UpdateSightings(kept, drifts, now);
            if (!sightings.SequenceEqual(kept)) SaveSightings(sightings);
            return new DriftSightings(sightings, Readable: true);
        }
    }

    /// <summary>
    /// The sightings after a look that found <paramref name="drifts"/>: one opened for each drift with no open sighting
    /// of the same value, each open one whose drift is no longer there closed at <paramref name="now"/> — marked
    /// <see cref="DriftSighting.ChangedAgain"/> when its setting is still changed, to another value — and each closed
    /// longer ago than Recent Changes looks back dropped. An open one is kept however old: it is what stops the same
    /// drift being reported again as new. At most <see cref="MaxSightings"/>, the earliest to end going first. Pure.
    /// </summary>
    internal static List<DriftSighting> UpdateSightings(IReadOnlyList<DriftSighting> kept, IReadOnlyList<SettingDrift> drifts,
                                                         DateTime now)
    {
        var sightings = kept
            .Where(s => s.GoneAt is not { } gone || now - gone <= RecentChangesService.KeptFor)
            .Select(s => s.GoneAt is null && !drifts.Any(d => Matches(s, d))
                ? s with { GoneAt = now, ChangedAgain = drifts.Any(d => d.Setting.Key == s.Key) }
                : s)
            .ToList();
        foreach (var drift in drifts.Where(d => !sightings.Any(s => s.GoneAt is null && Matches(s, d))))
            sightings.Add(new DriftSighting(drift.Setting.Key, drift.BaselineValue, drift.CurrentValue, now, null));

        var excess = sightings.Count - MaxSightings;
        if (excess > 0)
        {
            var earliestEnded = sightings.Where(s => s.GoneAt is not null).OrderBy(s => s.GoneAt).Take(excess).ToHashSet();
            sightings.RemoveAll(earliestEnded.Contains);
        }
        return sightings;

        static bool Matches(DriftSighting s, SettingDrift d) =>
            s.Key == d.Setting.Key && s.Baseline == d.BaselineValue && s.Value == d.CurrentValue;
    }

    /// <summary>The sightings in <paramref name="json"/>, or null when it is not a list of them.</summary>
    internal static List<DriftSighting>? ParseSightings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DriftSighting>>(json)?.Where(s => s is { Key.Length: > 0 }).ToList();
        }
        catch (JsonException ex)
        {
            Log.Debug("Settings drift sightings unreadable: {Error}", ex.Message);
            return null;
        }
    }

    private void SaveSightings(List<DriftSighting> sightings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sightingsPath)!);
            AtomicFile.WriteAllText(_sightingsPath, JsonSerializer.Serialize(sightings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Settings drift sightings not saved: {Error}", ex.Message);
        }
    }

    private void ForgetSightings()
    {
        try
        {
            if (File.Exists(_sightingsPath)) File.Delete(_sightingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Settings drift sightings from the old baseline not removed: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Loads the saved baseline, or null when none exists yet or the file could not be read or parsed.
    /// <see cref="BaselineFileExists"/> tells those apart.
    /// </summary>
    public BaselineSnapshot? LoadBaseline()
    {
        try
        {
            if (!File.Exists(_baselinePath)) return null;
            var snapshot = JsonSerializer.Deserialize<BaselineSnapshot>(File.ReadAllText(_baselinePath));
            // A baseline file that parses as JSON but omits the "Values" property deserializes
            // with Values == null (System.Text.Json does not enforce non-null on positional
            // record params). Normalize it to an empty map so downstream diffing never NREs on
            // a malformed-but-parseable file.
            return snapshot is { Values: null } ? snapshot with { Values = [] } : snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Debug("Settings baseline load failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public bool BaselineFileExists => File.Exists(_baselinePath);

    /// <summary>
    /// Compares the saved baseline against the live system and returns the drifted settings.
    /// Returns an empty list when there is no baseline or nothing changed.
    /// </summary>
    public IReadOnlyList<SettingDrift> DetectDrift()
    {
        var baseline = LoadBaseline();
        return baseline is null ? [] : DetectDrift(baseline, ReadCurrent());
    }

    /// <summary>
    /// Drift against a snapshot the caller already read. The no-argument overload above is the
    /// convenience path for callers that only want drifts and display nothing else.
    /// </summary>
    public IReadOnlyList<SettingDrift> DetectDrift(
        BaselineSnapshot baseline, IReadOnlyDictionary<string, int?> current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        return DetectChanges(Catalog, baseline.Values, current);
    }

    /// <summary>
    /// Writes the baseline value back for a single drifted setting. Returns true on success,
    /// false if the value can't be restored (read-only, no baseline value, or write denied).
    /// </summary>
    public bool Restore(SettingDrift drift)
    {
        ArgumentNullException.ThrowIfNull(drift);
        if (!drift.CanWriteBack || drift.BaselineValue is not { } baseline) return false;

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
            key.SetValue(drift.Setting.ValueName, baseline, RegistryValueKind.DWord);
            Log.Information("Settings Watchdog restored {Name} to {Value}",
                drift.Setting.Name, baseline);
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

    // Instance, because the baseline path is now per-instance (the configDir seam).
    // A failed write propagates. It used to be caught here and logged at Debug, and the tab then announced
    // "Baseline saved" over a baseline that was not on disk (#2452). The only caller is a user's Save click,
    // which can report the reason.
    private void Persist(BaselineSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_baselinePath)!);
        AtomicFile.WriteAllText(_baselinePath, JsonSerializer.Serialize(snapshot, JsonDefaults.Indented));
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
    /// <summary>Renders a drift table as CSV with a header row.</summary>
    /// <remarks>
    /// This export exists for a specific moment: Restore is about to overwrite the "now" column, so the file
    /// is the only record of what Windows changed. It therefore carries the labels the tab shows rather than
    /// the raw registry integers, plus whether each row can be restored — a drift the app cannot undo is the
    /// one the user most needs to keep a note of.
    /// </remarks>
    public static string ToCsv(IEnumerable<SettingDrift> drifts)
    {
        ArgumentNullException.ThrowIfNull(drifts);

        var sb = new StringBuilder();
        Csv.AppendRow(sb, "Setting", "Category", "Description", "Was", "Now", "Can restore");
        foreach (var d in drifts)
        {
            Csv.AppendRow(sb,
                d.Setting.Name,
                d.Setting.Category,
                d.Setting.Description,
                d.BaselineLabel,
                d.CurrentLabel,
                d.CanRestore ? "yes" : "no");
        }
        return sb.ToString();
    }
}

/// <summary>The saved baseline of watched setting values, with when it was captured.</summary>
public sealed record BaselineSnapshot(DateTime TakenAt, Dictionary<string, int?> Values);

/// <summary>When one drift was first seen and, once it is gone, when that was seen.</summary>
/// <param name="Key">The watched setting's <see cref="WatchedSetting.Key"/>.</param>
/// <param name="Baseline">The value in the saved baseline.</param>
/// <param name="Value">The value it was found with.</param>
/// <param name="FirstSeen">The first look that found it.</param>
/// <param name="GoneAt">The first look that found it gone, or null while it is still there.</param>
/// <param name="ChangedAgain">
/// True when it went because the setting changed to yet another value rather than back to the saved one; that value has
/// a sighting of its own.
/// </param>
public sealed record DriftSighting(string Key, int? Baseline, int? Value, DateTime FirstSeen, DateTime? GoneAt,
                                   bool ChangedAgain = false);

/// <summary>The sightings kept, or that they could not be read, in which case none were recorded.</summary>
public sealed record DriftSightings(IReadOnlyList<DriftSighting> Sightings, bool Readable)
{
    public static readonly DriftSightings Unreadable = new([], false);
}
