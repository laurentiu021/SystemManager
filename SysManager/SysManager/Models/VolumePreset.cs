// SysManager · VolumePreset
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A saved snapshot of per-application volume/mute settings the user can re-apply later — e.g. a
/// "Gaming" preset (game loud, music quiet) or a "Focus" preset. Persisted as JSON under
/// <c>%LocalAppData%\SysManager</c> by <c>VolumePresetService</c>. Apps are keyed by executable
/// name (not PID, which Windows recycles, and not the session id, which changes between runs) so a
/// preset re-applies across restarts to whatever instance of the app is running.
/// </summary>
public sealed record VolumePreset(
    string Name,
    IReadOnlyList<VolumePresetEntry> Entries);

/// <summary>One app's saved volume/mute within a <see cref="VolumePreset"/>, keyed by exe name.</summary>
public sealed record VolumePresetEntry(
    string ExecutableName,
    string DisplayName,
    float Volume,
    bool IsMuted);
