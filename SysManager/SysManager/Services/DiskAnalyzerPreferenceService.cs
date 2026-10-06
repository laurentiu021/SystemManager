// SysManager · DiskAnalyzerPreferenceService — remembers whether the Disk Analyzer map is shown
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// The Disk Analyzer's display settings, as stored on disk.
/// </summary>
/// <param name="ShowMap">Whether the map of folder sizes is shown above the list (#1592).</param>
public sealed record DiskAnalyzerPreference(bool ShowMap);

/// <summary>
/// Persists whether the Disk Analyzer shows its map. On a small screen "Hide map" gives the list its full height
/// back, and a choice like that has to survive a restart or the user makes it again every time.
/// <para>Stored beside the other per-user state in <c>%LOCALAPPDATA%\SysManager</c>, in the same shape as
/// <see cref="StandbyPreferenceService"/>: injectable directory, pure testable Serialize/Parse, file IO that never
/// throws. Anything unreadable falls back to showing the map. A file that could not be read is not written over,
/// and one that does not parse is kept aside before it is (#2521).</para>
/// </summary>
public sealed class DiskAnalyzerPreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The file name, beside the other per-user state.</summary>
    internal const string FileName = "disk-analyzer-preference.json";

    private readonly string _path;

    // What Load found, for Save. After a load that could not read the file, the setting on screen is the default and
    // the file still holds the user's own, so Save does not write over it. A file that does not parse is set aside
    // before the first write (#2521).
    private bool _unreadAtLoad;
    private bool _unparsableAtLoad;

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public DiskAnalyzerPreferenceService(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _path = Path.Combine(dir, FileName);
    }

    /// <summary>The default: the map is shown, which is what the tab is for.</summary>
    public static DiskAnalyzerPreference Default => new(ShowMap: true);

    /// <summary>
    /// Loads the saved settings, or <see cref="Default"/> when nothing is stored or the file cannot be trusted. Never
    /// throws.
    /// </summary>
    /// <remarks>What it found decides what <see cref="Save"/> may do with the file.</remarks>
    public DiskAnalyzerPreference Load()
    {
        var text = StoreFile.ReadText(_path);
        var parsed = text is null ? null : TryParse(text);
        _unreadAtLoad = text is null;
        _unparsableAtLoad = text is not null && parsed is null;
        return parsed ?? Default;
    }

    /// <summary>
    /// Saves the settings. Never throws. Returns false when nothing was written: the file could not be read when it
    /// was loaded, a file that did not parse could not be set aside, or the write failed.
    /// </summary>
    public bool Save(DiskAnalyzerPreference preference)
    {
        if (_unreadAtLoad)
        {
            Log.Debug("Disk Analyzer preference not saved: the file could not be read when it was loaded");
            return false;
        }
        if (_unparsableAtLoad && !StoreFile.SetAside(_path)) return false;
        _unparsableAtLoad = false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, Serialize(preference));
            return true;
        }
        catch (IOException ex) { Log.Debug("Disk Analyzer preference save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Disk Analyzer preference save denied: {Error}", ex.Message); }
        return false;
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    /// <summary>Serializes the settings to indented JSON.</summary>
    public static string Serialize(DiskAnalyzerPreference preference) =>
        JsonSerializer.Serialize(preference, JsonOptions);

    /// <summary>Parses the settings, falling back to <see cref="Default"/> for null, blank, or malformed input.</summary>
    public static DiskAnalyzerPreference Parse(string? json) => TryParse(json) ?? Default;

    /// <summary>
    /// <see cref="Parse"/>, except that input which is not settings at all is null rather than <see cref="Default"/>,
    /// so <see cref="Load"/> can tell a file to set aside from one that holds nothing.
    /// </summary>
    internal static DiskAnalyzerPreference? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            return JsonSerializer.Deserialize<DiskAnalyzerPreference>(json) ?? Default;
        }
        catch (JsonException ex) { Log.Debug("Disk Analyzer preference parse failed: {Error}", ex.Message); return null; }
    }
}
