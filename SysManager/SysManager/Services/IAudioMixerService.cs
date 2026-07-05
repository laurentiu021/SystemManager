// SysManager · IAudioMixerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="AudioMixerService"/> — enumerates per-application audio
/// sessions on the default render endpoint and gets/sets their volume, mute, and peak
/// level via Windows Core Audio. Extracting this interface lets
/// <c>AudioMixerViewModel</c>'s command and reconcile paths be unit-tested with a
/// substituted service against a deterministic session list, so no real audio hardware
/// or COM is touched in tests (Gate-ARCH: the mockable seam). All COM types stay inside
/// the concrete <see cref="AudioMixerService"/>; nothing here exposes them.
/// </summary>
public interface IAudioMixerService
{
    /// <summary>
    /// Snapshot the current per-app sessions on the default render endpoint. Expired
    /// sessions are dropped; active and inactive ones are returned. Never throws for a
    /// transient device/process fault — returns an empty list instead.
    /// </summary>
    IReadOnlyList<AudioSessionInfo> GetSessions();

    /// <summary>
    /// Set a session's master volume (clamped to 0.0–1.0). No-ops if the session no
    /// longer exists. Returns true if the change was applied.
    /// </summary>
    bool SetVolume(string sessionId, float level);

    /// <summary>
    /// Set a session's mute state. No-ops if the session no longer exists. Returns true
    /// if the change was applied.
    /// </summary>
    bool SetMute(string sessionId, bool muted);

    /// <summary>
    /// Read a session's current peak sample amplitude (0.0–1.0) for the VU meter, or 0
    /// if the session is gone or the value can't be read. Cheap enough to poll.
    /// </summary>
    float GetPeak(string sessionId);
}
