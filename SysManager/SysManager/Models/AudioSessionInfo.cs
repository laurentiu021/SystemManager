// SysManager · AudioSessionInfo
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// An immutable snapshot of one per-application audio session on the default render
/// endpoint, returned by <c>IAudioMixerService</c>. Carries no COM types so the mixer
/// ViewModel and its tests never touch Core Audio directly.
///
/// <para><see cref="SessionId"/> is the session <em>instance</em> identifier — a string
/// that stays stable across refreshes for the same stream, so it is the correlation key
/// the ViewModel reconciles rows by (not the PID, which is reused and shared between
/// multiple sessions of one app). <see cref="Volume"/> and <see cref="PeakLevel"/> are
/// normalized 0.0–1.0.</para>
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
