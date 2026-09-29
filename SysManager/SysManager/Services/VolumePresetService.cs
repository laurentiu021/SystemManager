// SysManager · VolumePresetService — save/load per-app volume presets as JSON
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Persists named per-application volume presets to a single JSON file under
/// <c>%LocalAppData%\SysManager\volume-presets.json</c>. A preset captures each app's volume + mute
/// keyed by executable name (stable across restarts, unlike PID/session id), so re-applying a
/// "Gaming" or "Focus" preset works on whatever instance of the app is running. Save/load/delete +
/// the merge that maps a preset onto the live sessions are pure and unit-tested; the file IO is
/// isolated and never throws to the caller. A file that could not be read is never written over: it
/// holds every preset (#2521). Strictly local — the file stays on the machine.
/// </summary>
public sealed class VolumePresetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;

    /// <summary>
    /// Serializes <see cref="Save"/> against <see cref="Delete"/>. Both are a <see cref="Load"/>
    /// followed by a <see cref="Persist"/> over one file holding every preset, so each writes back a
    /// whole-list snapshot. <see cref="AtomicFile"/> makes each WRITE atomic but not the read-then-write
    /// pair: without this, whichever saves last persists a snapshot taken before the other landed, and
    /// the other preset is silently gone — from disk, and from the on-screen list, because both methods
    /// return the merged list for the view-model to rebind.
    /// <para>Nothing reachable today overlaps them — both callers are synchronous relay commands on the
    /// Audio Mixer tab behind a modal confirm, with no await between the read and the write, so the UI
    /// thread serializes them outright. This makes it a property of the service instead, because a bulk
    /// save, a preset import, or simply making either command async re-opens it, and the failure is a
    /// preset the user saved that is not there next time — with no error, since <see cref="Persist"/>
    /// swallows <see cref="IOException"/> by design.</para>
    /// <para>Third service to need this, after <see cref="UpdateCheckPreferenceService"/> and
    /// <see cref="ServiceStartupLedgerService"/>;
    /// <c>ArchitectureTests.EveryStoreThatReadsThenWritesTheSameFile_SerializesThePair</c> now keeps the
    /// class closed.</para>
    /// </summary>
    private readonly Lock _mutateLock = new();

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public VolumePresetService(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _path = Path.Combine(dir, "volume-presets.json");
    }

    /// <summary>
    /// Loads all saved presets, newest-named-last (insertion order preserved), or null when the file is there and
    /// could not be read. A file that does not parse holds no preset that could be applied, so it loads as empty.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// Null is not "no presets". A failed read used to load as an empty list, and the next Save wrote a file
    /// holding only the preset it saved over every other one, while Delete wrote a file holding none (#2521).
    /// </remarks>
    public IReadOnlyList<VolumePreset>? Load() => Read().Presets;

    /// <summary>The presets, null when the file could not be read, and whether it was there but did not parse.</summary>
    private (IReadOnlyList<VolumePreset>? Presets, bool Unparsable) Read()
    {
        var text = StoreFile.ReadText(_path);
        if (text is null) return (null, false);
        return TryParse(text) is { } presets ? (presets, false) : ([], true);
    }

    /// <summary>
    /// Saves (adds or replaces by name, case-insensitive) a preset and persists the full set.
    /// Returns the updated list, or null when nothing was saved: the file could not be read, or one that does not
    /// parse could not be set aside, or the write failed. A blank name is rejected (returns the unchanged current
    /// set).
    /// <para>Serialized against <see cref="Delete"/> by <see cref="_mutateLock"/>. The blank-name
    /// rejection is inside it, unlike the equivalent guard in
    /// <see cref="ServiceStartupLedgerService.Remember"/>, because this one does not just return —
    /// it returns a <see cref="Load"/>, and the caller rebinds its list from whatever comes back.</para>
    /// </summary>
    public IReadOnlyList<VolumePreset>? Save(VolumePreset preset)
    {
        lock (_mutateLock)
        {
            var (presets, unparsable) = Read();
            if (presets is null || string.IsNullOrWhiteSpace(preset.Name)) return presets;
            if (unparsable && !StoreFile.SetAside(_path)) return null;
            var merged = Upsert(presets, preset);
            return Persist(merged) ? merged : null;
        }
    }

    /// <summary>
    /// Deletes the named preset (case-insensitive) and persists. Returns the updated list, or null when nothing was
    /// deleted, for the same reasons as <see cref="Save"/>.
    /// Serialized against <see cref="Save"/> by <see cref="_mutateLock"/>.
    /// </summary>
    public IReadOnlyList<VolumePreset>? Delete(string name)
    {
        lock (_mutateLock)
        {
            var (presets, unparsable) = Read();
            if (presets is null) return null;
            if (unparsable && !StoreFile.SetAside(_path)) return null;
            var remaining = presets.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return Persist(remaining) ? remaining : null;
        }
    }

    private bool Persist(IReadOnlyList<VolumePreset> presets)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, Serialize(presets));
            return true;
        }
        catch (IOException ex) { Log.Debug("Volume presets save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Volume presets save denied: {Error}", ex.Message); }
        return false;
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    /// <summary>Serializes the preset list to indented JSON.</summary>
    public static string Serialize(IReadOnlyList<VolumePreset> presets) => JsonSerializer.Serialize(presets, JsonOptions);

    /// <summary>Parses the preset list from JSON; returns empty for null/blank/malformed input.</summary>
    public static IReadOnlyList<VolumePreset> Parse(string? json) => TryParse(json) ?? [];

    /// <summary><see cref="Parse"/>, except that input which is not a preset list at all is null rather than empty.</summary>
    internal static IReadOnlyList<VolumePreset>? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<VolumePreset>>(json, JsonOptions) ?? []; }
        catch (JsonException ex) { Log.Debug("Volume presets parse failed: {Error}", ex.Message); return null; }
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
