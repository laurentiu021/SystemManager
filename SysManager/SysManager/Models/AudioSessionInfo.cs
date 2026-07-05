// SysManager · AudioSessionInfo
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// An immutable snapshot of one per-application audio session on the default render
/// endpoint, returned by <c>IAudioMixerService</c>. Carries no COM types so the mixer
/// ViewModel and its tests never touch Core Audio directly.
///
/// <para><see cref="SessionId"/> is a per-app group key derived from the Core Audio session
/// <em>instance</em> identifier (with the trailing per-stream GUID stripped so all of one
/// process's streams collapse to a single row). It stays stable across refreshes and — unlike
/// the raw PID, which Windows recycles — it will not map two different apps onto the same row,
/// so it is the correlation key the ViewModel reconciles rows by. (When the instance id is
/// unavailable it falls back to <c>"pid:&lt;pid&gt;"</c>, and the system-sounds pseudo-session
/// uses the fixed <c>"system-sounds"</c> sentinel.) <see cref="Volume"/> and
/// <see cref="PeakLevel"/> are normalized 0.0–1.0.</para>
/// </summary>
public sealed record AudioSessionInfo(
    string SessionId,
    uint ProcessId,
    string DisplayName,
    string ExePath,
    float Volume,
    bool IsMuted,
    AudioSessionState State,
    bool IsSystemSounds,
    float PeakLevel);
