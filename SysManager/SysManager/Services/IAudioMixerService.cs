// SysManager · IAudioMixerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="AudioMixerService"/> — enumerates per-application audio
/// sessions on the default render endpoint and gets/sets their volume, mute, and peak
/// level via Windows Core Audio, along with the whole PC's volume and the device all sound
/// plays through. Extracting this interface lets
/// <c>AudioMixerViewModel</c>'s command and reconcile paths be unit-tested with a
/// substituted service against a deterministic session list, so no real audio hardware
/// or COM is touched in tests (the mockable seam). All COM types stay inside
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
    /// Set a session's volume (clamped to 0.0–1.0). No-ops if the session no
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
    /// if the session is gone or the value can't be read.
    /// <para>Each call marshals a cross-apartment COM call per audio control in the group, so prefer
    /// <see cref="GetPeaks"/> when reading more than one session at a time.</para>
    /// </summary>
    float GetPeak(string sessionId);

    /// <summary>
    /// Read the peak amplitude for many sessions in one pass, returning a value for every id asked for
    /// (0 for one that is gone or unreadable).
    /// <para>Exists because the VU meters refresh 20 times a second: calling <see cref="GetPeak"/> per
    /// row took the service lock once per row per tick, and each acquisition contends with the 1 Hz
    /// reconcile that holds the same lock while enumerating every session. One call takes the lock once
    /// and does all the COM work inside it, so a tab showing ten apps costs one hop instead of ten.</para>
    /// </summary>
    IReadOnlyDictionary<string, float> GetPeaks(IEnumerable<string> sessionIds);

    /// <summary>
    /// The whole PC's volume and mute: those of the default output device, which apply to every app on top of
    /// its own level — what the Windows taskbar slider moves. Null when there is no output device or it could
    /// not be read. Never throws.
    /// </summary>
    PcVolumeInfo? GetPcVolume();

    /// <summary>
    /// Set the whole PC's volume (clamped to 0.0–1.0). Returns true if Windows applied it; false when the
    /// output device has not been read yet or is gone.
    /// </summary>
    bool SetPcVolume(float level);

    /// <summary>
    /// Mute or unmute the whole PC. Returns true if Windows applied it; false when the output device has not
    /// been read yet or is gone.
    /// </summary>
    bool SetPcMute(bool muted);

    /// <summary>
    /// The default output device's current peak (0.0–1.0) for the whole-PC level meter, or 0 when it cannot be
    /// read. Only reads a device <see cref="GetPcVolume"/> has already opened, so the 50&#160;ms meter loop never
    /// opens one itself.
    /// </summary>
    float GetPcPeak();

    /// <summary>
    /// Enumerate the active render (output) devices via the documented Core Audio device API.
    /// Never throws — returns an empty list on a transient device fault. The one flagged
    /// <see cref="Models.AudioDevice.IsDefault"/> is the current system default.
    /// </summary>
    IReadOnlyList<Models.AudioDevice> GetRenderDevices();

    /// <summary>
    /// True when SysManager can switch the device all sound plays through on this Windows build — i.e. the
    /// (undocumented) <c>IPolicyConfig</c> interface bound. When false, the UI names the current device and
    /// guides the user to Windows' sound settings, and <see cref="SetDefaultOutputDevice"/> will no-op.
    /// </summary>
    bool IsDefaultOutputSwitchSupported { get; }

    /// <summary>
    /// Make <paramref name="deviceId"/> the device all sound plays through, for each of the three defaults
    /// Windows keeps (everyday, multimedia and calls), as the Windows sound flyout does. Apps routed to a device
    /// of their own keep it. Returns true only when Windows applied all three; false (a no-op) when switching is
    /// not supported. Never throws.
    /// </summary>
    bool SetDefaultOutputDevice(string deviceId);

    /// <summary>
    /// True when true in-app per-app output-device routing is available on this Windows build —
    /// i.e. the (undocumented) <c>IAudioPolicyConfigFactory</c> interface bound successfully. When false,
    /// the UI must fall back to guiding the user to Windows' per-app sound settings, and
    /// <see cref="SetSessionOutputDevice"/> will no-op.
    /// </summary>
    bool IsRoutingSupported { get; }

    /// <summary>
    /// Reads the endpoint id this session is currently routed to.
    /// </summary>
    /// <returns>
    /// The endpoint id when the app has an override; <see cref="string.Empty"/> when the read succeeded and
    /// there is no override, so the app follows the system default; <c>null</c> when the route could not be
    /// read at all.
    /// </returns>
    /// <remarks>
    /// Three states, not two. The old contract folded "could not read" into the empty string alongside
    /// "follows the default", and the caller turned an empty string into the default device's name — so a
    /// failed read was displayed as a confident claim about the user's system. Callers must render
    /// <c>null</c> as unknown.
    /// </remarks>
    string? GetSessionOutputDevice(string sessionId);

    /// <summary>
    /// Routes a session's app to a specific output device (empty <paramref name="deviceId"/> =
    /// follow the system default). Returns true only when the change was applied via
    /// <c>IAudioPolicyConfigFactory</c>; returns false (a no-op) when routing isn't supported, so the
    /// caller can fall back to the guided path. Never throws.
    /// </summary>
    bool SetSessionOutputDevice(string sessionId, string deviceId);
}
