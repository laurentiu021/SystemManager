// SysManager · StandbyPreferenceService — remembers the standby auto-purge settings
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// The user's standby-list auto-purge settings, as stored on disk.
/// </summary>
/// <param name="AutoPurgeEnabled">Whether auto-purge should be armed on startup.</param>
/// <param name="ThresholdMb">Available-RAM threshold, in MB, below which auto-purge fires.</param>
public sealed record StandbyPreference(bool AutoPurgeEnabled, double ThresholdMb);

/// <summary>
/// Persists the Standby List Cleaner's auto-purge toggle and threshold. Auto-purge is a
/// set-and-forget setting, so losing it on every restart made it effectively unusable: the
/// user armed it, closed the app, and it silently reverted to off at 1 GB.
/// <para>Stored beside the other per-user state in <c>%LOCALAPPDATA%\SysManager</c>, following
/// the same shape as <see cref="ClosePreferenceService"/> and <see cref="VolumePresetService"/>:
/// injectable directory, pure testable Serialize/Parse, file IO that never throws. An
/// unreadable or out-of-range value falls back to the safe default (auto-purge off) rather than
/// arming an automatic action the user did not choose.</para>
/// </summary>
public sealed class StandbyPreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Default threshold when nothing has been saved — 1 GB, matching the previous default.</summary>
    public const double DefaultThresholdMb = 1024;

    /// <summary>
    /// Lowest accepted threshold. Below this, auto-purge would fire almost continuously on a
    /// machine under memory pressure, so a value this small is treated as corrupt.
    /// </summary>
    internal const double MinThresholdMb = 64;

    /// <summary>
    /// Highest accepted threshold — 1 TB. Above any real machine's RAM, so auto-purge would fire
    /// on every tick; a value this large is treated as corrupt rather than honoured.
    /// </summary>
    internal const double MaxThresholdMb = 1024 * 1024;

    private readonly string _path;

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public StandbyPreferenceService(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _path = Path.Combine(dir, "standby-preference.json");
    }

    /// <summary>The safe default: auto-purge off, threshold at 1 GB.</summary>
    public static StandbyPreference Default => new(false, DefaultThresholdMb);

    /// <summary>
    /// Loads the saved settings, or <see cref="Default"/> when nothing is stored or the file
    /// cannot be trusted. Never throws.
    /// </summary>
    public StandbyPreference Load()
    {
        try
        {
            if (!File.Exists(_path)) return Default;
            return Parse(File.ReadAllText(_path));
        }
        catch (IOException ex) { Log.Debug("Standby preference load failed: {Error}", ex.Message); return Default; }
        catch (UnauthorizedAccessException ex) { Log.Debug("Standby preference load denied: {Error}", ex.Message); return Default; }
    }

    /// <summary>Saves the settings. Never throws; a failure just means the value is not remembered.</summary>
    public void Save(StandbyPreference preference)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, Serialize(preference));
        }
        catch (IOException ex) { Log.Debug("Standby preference save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Standby preference save denied: {Error}", ex.Message); }
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    /// <summary>Serializes the settings to indented JSON.</summary>
    public static string Serialize(StandbyPreference preference) =>
        JsonSerializer.Serialize(preference, JsonOptions);

    /// <summary>
    /// Parses the settings, falling back to <see cref="Default"/> for null, blank, or malformed
    /// input. An out-of-range threshold resets only that field — the toggle is still honoured —
    /// but a value that cannot be read at all disarms auto-purge, because arming an automatic
    /// system action on the strength of a corrupt file is the wrong way to fail.
    /// </summary>
    public static StandbyPreference Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var stored = JsonSerializer.Deserialize<StandbyPreference>(json);
            if (stored is null) return Default;

            var threshold = double.IsFinite(stored.ThresholdMb)
                && stored.ThresholdMb is >= MinThresholdMb and <= MaxThresholdMb
                    ? stored.ThresholdMb
                    : DefaultThresholdMb;

            return new StandbyPreference(stored.AutoPurgeEnabled, threshold);
        }
        catch (JsonException ex) { Log.Debug("Standby preference parse failed: {Error}", ex.Message); return Default; }
    }
}
