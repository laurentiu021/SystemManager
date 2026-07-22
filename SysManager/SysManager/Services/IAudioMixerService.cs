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

    /// <summary>
    /// Enumerate the active render (output) devices via the documented Core Audio device API.
    /// Never throws — returns an empty list on a transient device fault. The one flagged
    /// <see cref="Models.AudioDevice.IsDefault"/> is the current system default.
    /// </summary>
    IReadOnlyList<Models.AudioDevice> GetRenderDevices();

    /// <summary>
    /// True when true in-app per-app output-device routing is available on this Windows build —
    /// i.e. the (undocumented) <c>IAudioPolicyConfig</c> interface bound successfully. When false,
    /// the UI must fall back to guiding the user to Windows' per-app sound settings, and
    /// <see cref="SetSessionOutputDevice"/> will no-op.
    /// </summary>
    bool IsRoutingSupported { get; }

    /// <summary>
    /// Reads the endpoint id this session is currently routed to, or an empty string for
    /// "follow the system default" (or when routing can't be read). Best-effort.
    /// </summary>
    string GetSessionOutputDevice(string sessionId);

    /// <summary>
    /// Routes a session's app to a specific output device (empty <paramref name="deviceId"/> =
    /// follow the system default). Returns true only when the change was applied via
    /// <c>IAudioPolicyConfig</c>; returns false (a no-op) when routing isn't supported, so the
    /// caller can fall back to the guided path. Never throws.
    /// </summary>
    bool SetSessionOutputDevice(string sessionId, string deviceId);
}
