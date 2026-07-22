// SysManager · VolumePresetService — save/load per-app volume presets as JSON
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Persists named per-application volume presets to a single JSON file under
/// <c>%LocalAppData%\SysManager\volume-presets.json</c>. A preset captures each app's volume + mute
/// keyed by executable name (stable across restarts, unlike PID/session id), so re-applying a
/// "Gaming" or "Focus" preset works on whatever instance of the app is running. Save/load/delete +
/// the merge that maps a preset onto the live sessions are pure and unit-tested; the file IO is
/// isolated and never throws to the caller (returns defaults on error). Strictly local — the file
/// stays on the machine.
/// </summary>
public sealed class VolumePresetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public VolumePresetService(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _path = Path.Combine(dir, "volume-presets.json");
    }

    /// <summary>Loads all saved presets, newest-named-last (insertion order preserved). Never throws.</summary>
    public IReadOnlyList<VolumePreset> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return Parse(File.ReadAllText(_path));
        }
        catch (IOException ex) { Log.Debug("Volume presets load failed: {Error}", ex.Message); return []; }
        catch (UnauthorizedAccessException ex) { Log.Debug("Volume presets load denied: {Error}", ex.Message); return []; }
    }

    /// <summary>
    /// Saves (adds or replaces by name, case-insensitive) a preset and persists the full set.
    /// Returns the updated list. A blank name is rejected (returns the unchanged current set).
    /// </summary>
    public IReadOnlyList<VolumePreset> Save(VolumePreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name)) return Load();
        var merged = Upsert(Load(), preset);
        Persist(merged);
        return merged;
    }

    /// <summary>Deletes the named preset (case-insensitive) and persists. Returns the updated list.</summary>
    public IReadOnlyList<VolumePreset> Delete(string name)
    {
        var remaining = Load().Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        Persist(remaining);
        return remaining;
    }

    private void Persist(IReadOnlyList<VolumePreset> presets)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, Serialize(presets));
        }
        catch (IOException ex) { Log.Debug("Volume presets save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Volume presets save denied: {Error}", ex.Message); }
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    /// <summary>Serializes the preset list to indented JSON.</summary>
    public static string Serialize(IReadOnlyList<VolumePreset> presets) => JsonSerializer.Serialize(presets, JsonOptions);

    /// <summary>Parses the preset list from JSON; returns empty for null/blank/malformed input.</summary>
    public static IReadOnlyList<VolumePreset> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<VolumePreset>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Adds <paramref name="preset"/> to <paramref name="existing"/>, replacing any preset with the
    /// same name (case-insensitive) in place, else appending. Pure so the replace-vs-append rule is
    /// unit-tested. Returns a new list; the input is not mutated.
    /// </summary>
    public static IReadOnlyList<VolumePreset> Upsert(IReadOnlyList<VolumePreset> existing, VolumePreset preset)
    {
        var result = existing.ToList();
        int i = result.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) result[i] = preset;
        else result.Add(preset);
        return result;
    }

    /// <summary>
    /// Builds the concrete volume/mute writes to apply a preset to the live sessions: for each live
    /// session whose executable name matches a preset entry (case-insensitive), returns its session
    /// id with the preset's target volume + mute. Sessions with no matching entry are left untouched
    /// (not returned). Pure — the caller performs the actual <c>SetVolume</c>/<c>SetMute</c> writes.
    /// </summary>
    public static IReadOnlyList<(string SessionId, float Volume, bool IsMuted)> BuildApplyPlan(
        VolumePreset preset, IReadOnlyList<AudioSessionInfo> liveSessions)
    {
        var byExe = new Dictionary<string, VolumePresetEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in preset.Entries)
            if (!string.IsNullOrEmpty(e.ExecutableName)) byExe[e.ExecutableName] = e;

        var plan = new List<(string, float, bool)>();
        foreach (var s in liveSessions)
        {
            var exe = ExeName(s.ExePath);
            if (exe.Length > 0 && byExe.TryGetValue(exe, out var entry))
                plan.Add((s.SessionId, Math.Clamp(entry.Volume, 0f, 1f), entry.IsMuted));
        }
        return plan;
    }

    /// <summary>Extracts the bare executable file name from a full path (empty if none). Case preserved.</summary>
    public static string ExeName(string? exePath)
        => string.IsNullOrWhiteSpace(exePath) ? string.Empty : Path.GetFileName(exePath);
}
