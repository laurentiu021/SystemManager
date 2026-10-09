// SysManager · AudioMixerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads and controls per-application audio via Windows Core Audio (WASAPI) on the
/// <b>default render endpoint</b>. Enumerates the render sessions, groups them by owning
/// process (the Windows Volume Mixer mental model — one row per app), and gets/sets each
/// group's volume, mute, and VU peak. Uses raw <c>[ComImport]</c> interop for the seven
/// documented interfaces (<c>IMMDeviceEnumerator</c> → <c>IAudioSessionManager2</c> →
/// <c>IAudioSessionEnumerator</c> → <c>IAudioSessionControl(2)</c> /
/// <c>ISimpleAudioVolume</c> / <c>IAudioMeterInformation</c>, and <c>IAudioEndpointVolume</c>) so
/// nothing but the .NET runtime is added to the single portable .exe.
///
/// <para>Scope: enumerates sessions on the default render endpoint. Output devices are listed via
/// the documented device API (<see cref="GetRenderDevices"/>). Per-app output-device routing uses
/// the UNDOCUMENTED <c>IAudioPolicyConfigFactory</c> interface (the same one EarTrumpet reverse-engineers)
/// and is feature-detected at runtime: if it can't bind on this Windows build,
/// <see cref="IsRoutingSupported"/> is false and the UI falls back to guiding the user to Windows'
/// per-app sound settings. Volume presets live in a separate pure service.</para>
///
/// <para>The whole PC (#1588): its volume and mute come from the documented <c>IAudioEndpointVolume</c>, and
/// its level from <c>IAudioMeterInformation</c>, both activated on the same default endpoint the sessions are
/// read from. Switching the device all sound plays through uses the UNDOCUMENTED <c>IPolicyConfig</c> (see
/// <see cref="PolicyConfigClient"/>), feature-detected the same way as routing. The endpoint held open follows
/// the Windows default: a switch made here drops it at once, and one made elsewhere (headphones plugged in, the
/// taskbar flyout) is noticed on the next device enumeration, so the apps and the PC volume move with it.</para>
///
/// <para>Thread-safety: every COM access is guarded by <see cref="_gate"/>. The manager /
/// device / enumerator handle is held open across polls; the per-app session interfaces
/// are cached keyed by process and released deterministically on the next enumeration and
/// on <see cref="Dispose"/> (COM RCWs are released explicitly, never left to finalizers,
/// because this tab is created/destroyed on navigation — not app-lifetime).</para>
/// </summary>
public sealed class AudioMixerService : IAudioMixerService, IDisposable
{
    // Change-source token passed on every volume/mute write. Fixed per app instance so a
    // future event-notification path could recognise (and ignore) its own changes.
    private static readonly Guid EventContext = new("6f9a1c22-3b47-4e7a-9d5e-1f0c2a8b4d61");

    private readonly Lock _gate = new();
    private object? _enumerator;   // IMMDeviceEnumerator
    private object? _device;       // IMMDevice (default render endpoint)
    private object? _manager;      // IAudioSessionManager2
    private object? _endpointVolume; // IAudioEndpointVolume — the whole PC's volume, on _device
    private object? _endpointMeter;  // IAudioMeterInformation — the whole PC's level, on _device
    private string? _deviceId;     // _device's endpoint id, to notice Windows moving the default elsewhere
    private bool _disposed;

    // Per-app cached session interfaces, keyed by the group key (PID string, or the
    // "system-sounds" sentinel). Each app can own several render sessions; the group holds
    // all of their control RCWs so a single slider/mute drives every stream of the app.
    private readonly Dictionary<string, List<object>> _groups = new(StringComparer.Ordinal);

    // Per-group (session id → owning PID + exe name), captured during enumeration so routing can
    // build the app's audio-session identifier for IAudioPolicyConfigFactory without re-walking COM.
    private readonly Dictionary<string, (uint Pid, string ExeName)> _routingKeys = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public IReadOnlyList<AudioSessionInfo> GetSessions()
    {
        // Two phases, so the slow work never runs under the COM gate (which the UI thread's
        // GetPeak/SetVolume also take): (1) under _gate, do ONLY the fast COM reads and cache
        // the control RCWs; (2) after releasing the lock, resolve each app's name/icon-path
        // (Process/MainModule — potentially multi-ms) off the lock, cached by group key.
        List<GroupAccumulator> groups;
        lock (_gate)
        {
            if (_disposed) return [];

            groups = EnumerateGroupsLocked();
        }

        // Identity resolution is pure Process API — touches no audio COM object — so it is
        // safe (and correct) to run it outside _gate. GetSessions is only ever called from the
        // single serialized reconcile loop, so _identityCache needs no extra synchronization.
        var results = new List<AudioSessionInfo>(groups.Count);
        var routing = new Dictionary<string, (uint, string)>(groups.Count, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var (name, path) = g.IsSystemSounds
                ? ("System Sounds", string.Empty)
                : ResolveIdentityCached(g.GroupKey, g.ProcessId, g.PidKnown);
            results.Add(g.ToInfo(name, path));
            if (!g.IsSystemSounds)
                routing[g.GroupKey] = (g.ProcessId, System.IO.Path.GetFileName(path) ?? string.Empty);
        }
        // Publish the routing keys atomically under the gate (SetSessionOutputDevice reads them).
        lock (_gate)
        {
            _routingKeys.Clear();
            foreach (var kv in routing) _routingKeys[kv.Key] = kv.Value;
        }

        // Stable order so the reconcile diff is deterministic: system sounds last,
        // apps alphabetical.
        results.Sort(static (a, b) =>
        {
            if (a.IsSystemSounds != b.IsSystemSounds) return a.IsSystemSounds ? 1 : -1;
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    /// <summary>
    /// Phase 1 (under <see cref="_gate"/>): enumerate the render sessions, cache each session's
    /// control RCW keyed by its stable session-instance identifier, and read the fast COM values
    /// (volume/mute/state/peak/pid). Does NOT resolve process identity — that slow work is done by
    /// the caller after the lock is released. Returns the per-app accumulators.
    /// </summary>
    private List<GroupAccumulator> EnumerateGroupsLocked()
    {
        var acc = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);
        try
        {
            if (!EnsureManager()) return [];

            var mgr = (IAudioSessionManager2)_manager!;
            if (mgr.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum is null)
                return [];

            // Fresh enumeration → the old cached control RCWs are stale; release them.
            ReleaseGroups();

            try
            {
                if (sessionEnum.GetCount(out int count) != 0) return [];

                for (int i = 0; i < count; i++)
                {
                    if (sessionEnum.GetSession(i, out var control) != 0 || control is null)
                        continue;

                    if (control is not IAudioSessionControl2 ctl2)
                    {
                        Release(control);
                        continue;
                    }

                    // Drop dead streams; keep active + inactive.
                    if (ctl2.GetState(out int rawState) == 0 &&
                        (AudioSessionState)rawState == AudioSessionState.Expired)
                    {
                        Release(control);
                        continue;
                    }

                    bool isSystemSounds = ctl2.IsSystemSoundsSession() == 0; // S_OK == true
                    // Honor the HRESULT: on FAILURE (hr < 0) leave pid unknown so a genuine app
                    // whose PID couldn't be read isn't later mislabeled "System Sounds". Note any
                    // SUCCESS code counts — including AUDCLNT_S_NO_SINGLE_PROCESS (0x0008900F),
                    // returned for a session spanning several processes, where pid IS still valid.
                    bool pidKnown = ctl2.GetProcessId(out uint pid) >= 0;

                    // Key on the session-INSTANCE identifier (stable per stream, PID-reuse-proof)
                    // rather than the recyclable PID. All of an app's streams still collapse into
                    // one row: sessions of the same process share a common prefix in the instance
                    // id, so we group by the "…|<pid>|…%b<GUID>" up to the trailing per-stream GUID.
                    string key = isSystemSounds
                        ? "system-sounds"
                        : GroupKeyFor(ctl2, pid);

                    // Read this session's controls (same underlying COM object, so we keep
                    // ONE RCW per session and cast to the sibling interfaces on demand).
                    float volume = 0f;
                    bool muted = false;
                    if (control is ISimpleAudioVolume vol)
                    {
                        vol.GetMasterVolume(out volume);
                        vol.GetMute(out muted);
                    }
                    float peak = 0f;
                    if (control is IAudioMeterInformation meter)
                        meter.GetPeakValue(out peak);

                    var state = (AudioSessionState)rawState;

                    if (!acc.TryGetValue(key, out var group))
                    {
                        group = new GroupAccumulator(key, pid, pidKnown, isSystemSounds);
                        acc[key] = group;
                        _groups[key] = [];
                    }

                    _groups[key].Add(control);          // cache the RCW for set/get later
                    group.Absorb(volume, muted, state, peak);
                }
            }
            finally
            {
                Release(sessionEnum);
            }
        }
        catch (COMException ex)
        {
            // A transient device/session fault (e.g. device invalidated, RDP reconnect)
            // must not crash the tab — reset the handle so the next poll rebuilds it.
            Log.Debug("Audio session enumeration failed: {Error}", ex.Message);
            ResetManager();
            return [];
        }

        return [.. acc.Values];
    }

    /// <summary>
    /// Group key for an app session: the session-instance identifier with its trailing per-stream
    /// GUID stripped, so every stream of one process maps to one row (Windows Volume Mixer model)
    /// while remaining stable across refreshes and immune to PID reuse. Falls back to the PID when
    /// the instance id is unavailable.
    /// </summary>
    private static string GroupKeyFor(IAudioSessionControl2 ctl2, uint pid)
    {
        try
        {
            if (ctl2.GetSessionInstanceIdentifier(out string id) == 0 && !string.IsNullOrEmpty(id))
                return StripStreamGuid(id);
        }
        catch (COMException) { /* fall back to PID below */ }
        return "pid:" + pid;
    }

    /// <summary>
    /// Reduces a Core Audio session-instance identifier to a per-app-group key by dropping the
    /// trailing per-stream GUID. The identifier looks like <c>"…|…%b{stream-guid}"</c>; the part
    /// before the final <c>"%b"</c> is stable per app+session-group, so every stream of one
    /// process collapses to one row. Returns the input unchanged when there is no <c>"%b"</c>
    /// marker (or it is at the start). Extracted + internal so the load-bearing PID-reuse fix is
    /// unit-testable without a live audio endpoint.
    /// </summary>
    internal static string StripStreamGuid(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return instanceId;
        int marker = instanceId.LastIndexOf("%b", StringComparison.Ordinal);
        return marker > 0 ? instanceId[..marker] : instanceId;
    }

    /// <inheritdoc/>
    public bool SetVolume(string sessionId, float level)
    {
        float clamped = Math.Clamp(level, 0f, 1f);
        lock (_gate)
        {
            if (_disposed || !_groups.TryGetValue(sessionId, out var controls)) return false;

            bool any = false;
            var ctx = EventContext;
            foreach (var control in controls)
            {
                if (control is not ISimpleAudioVolume vol) continue;
                try { any |= vol.SetMasterVolume(clamped, ref ctx) == 0; }
                catch (COMException ex) { Log.Debug("SetVolume failed: {Error}", ex.Message); }
            }
            return any;
        }
    }

    /// <inheritdoc/>
    public bool SetMute(string sessionId, bool muted)
    {
        lock (_gate)
        {
            if (_disposed || !_groups.TryGetValue(sessionId, out var controls)) return false;

            bool any = false;
            var ctx = EventContext;
            foreach (var control in controls)
            {
                if (control is not ISimpleAudioVolume vol) continue;
                try { any |= vol.SetMute(muted, ref ctx) == 0; }
                catch (COMException ex) { Log.Debug("SetMute failed: {Error}", ex.Message); }
            }
            return any;
        }
    }

    /// <inheritdoc/>
    public float GetPeak(string sessionId)
    {
        lock (_gate)
        {
            return PeakLocked(sessionId);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, float> GetPeaks(IEnumerable<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        // Materialise BEFORE taking the lock: the caller's sequence is usually a LINQ projection over a
        // bound collection, and enumerating it inside the lock would run caller code — including UI-thread
        // collection reads — while holding the gate the 1 Hz reconcile also wants.
        var ids = sessionIds as IReadOnlyList<string> ?? [.. sessionIds];
        var peaks = new Dictionary<string, float>(ids.Count, StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var id in ids)
                peaks[id] = PeakLocked(id);
        }

        return peaks;
    }

    /// <summary>
    /// The COM read itself. Caller holds <c>_gate</c>; every id gets a value, 0 when the session is gone
    /// or unreadable.
    /// </summary>
    private float PeakLocked(string sessionId)
    {
        if (_disposed || !_groups.TryGetValue(sessionId, out var controls)) return 0f;

        float peak = 0f;
        foreach (var control in controls)
        {
            if (control is not IAudioMeterInformation meter) continue;
            try
            {
                if (meter.GetPeakValue(out float value) == 0 && value > peak) peak = value;
            }
            catch (COMException ex)
            {
                // Log once per session-handle generation, not per call. This is driven by the Volume
                // Control tab's 50 ms peak-meter loop, so one bad audio session wrote ~20 identical
                // Debug lines a second for as long as the tab stayed open — the log's single biggest
                // flood path. The message is identical every tick, so repeating it adds nothing;
                // ReleaseGroups resets the flag, meaning a genuinely new failure after a device change
                // is still reported. Mirrors the existing one-shot probe pattern
                // (_routingProbed / _routingSupported).
                if (!_peakFailureLogged)
                {
                    _peakFailureLogged = true;
                    Log.Debug("GetPeak failed: {Error}", ex.Message);
                }
            }
        }
        return peak;
    }

    // Reset by ReleaseGroups, so it is per session-handle generation rather than per process.
    private bool _peakFailureLogged;

    // ── The whole PC: volume, mute and level of the default endpoint (documented API) ──

    /// <inheritdoc/>
    public PcVolumeInfo? GetPcVolume()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            try
            {
                if (!EnsureManager() || !EnsureEndpointLocked()) return null;

                var volume = (IAudioEndpointVolume)_endpointVolume!;
                if (volume.GetMasterVolumeLevelScalar(out float level) == 0 && volume.GetMute(out bool muted) == 0)
                    return new PcVolumeInfo(level, muted);

                // The device stopped answering (unplugged, disabled): drop it, so the next pass opens whatever
                // Windows plays through now instead of reading a dead endpoint for the rest of the session.
                ResetManager();
                return null;
            }
            catch (COMException ex)
            {
                Log.Debug("PC volume read failed: {Error}", ex.Message);
                ResetManager();
                return null;
            }
        }
    }

    /// <summary>
    /// Opens the whole-PC volume and meter on the endpoint <see cref="EnsureManager"/> holds, once per endpoint.
    /// Caller holds <see cref="_gate"/> and has bound the manager. Reached only from <see cref="GetPcVolume"/>,
    /// which the tab calls on a worker thread, so these objects are created where the session objects are: the
    /// writes below run on the UI thread and only ever use what a read already opened.
    /// </summary>
    private bool EnsureEndpointLocked()
    {
        if (_endpointVolume is not null) return true;

        var device = (IMMDevice)_device!;
        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volume) != 0 || volume is not IAudioEndpointVolume)
        {
            Release(volume);
            return false;
        }
        _endpointVolume = volume;

        // The meter is a nicety: without it the PC's level bar stays dark and the volume still works.
        var meterIid = typeof(IAudioMeterInformation).GUID;
        if (device.Activate(ref meterIid, CLSCTX_ALL, IntPtr.Zero, out var meter) == 0 && meter is IAudioMeterInformation)
            _endpointMeter = meter;
        else
            Release(meter);
        return true;
    }

    /// <inheritdoc/>
    public bool SetPcVolume(float level)
    {
        float clamped = Math.Clamp(level, 0f, 1f);
        lock (_gate)
        {
            if (_disposed || _endpointVolume is not IAudioEndpointVolume volume) return false;
            var ctx = EventContext;
            try { return volume.SetMasterVolumeLevelScalar(clamped, ref ctx) == 0; }
            catch (COMException ex) { Log.Debug("SetPcVolume failed: {Error}", ex.Message); return false; }
        }
    }

    /// <inheritdoc/>
    public bool SetPcMute(bool muted)
    {
        lock (_gate)
        {
            if (_disposed || _endpointVolume is not IAudioEndpointVolume volume) return false;
            var ctx = EventContext;
            try { return volume.SetMute(muted, ref ctx) == 0; }
            catch (COMException ex) { Log.Debug("SetPcMute failed: {Error}", ex.Message); return false; }
        }
    }

    /// <inheritdoc/>
    public float GetPcPeak()
    {
        lock (_gate)
        {
            if (_disposed || _endpointMeter is not IAudioMeterInformation meter) return 0f;
            try
            {
                return meter.GetPeakValue(out float peak) == 0 ? peak : 0f;
            }
            catch (COMException ex)
            {
                // Once per endpoint, for the reason PeakLocked gives: the meter loop asks 20 times a second.
                if (!_pcPeakFailureLogged)
                {
                    _pcPeakFailureLogged = true;
                    Log.Debug("GetPcPeak failed: {Error}", ex.Message);
                }
                return 0f;
            }
        }
    }

    // Reset by ResetManager, which releases the meter it describes.
    private bool _pcPeakFailureLogged;

    // ── Output-device enumeration (documented API) ─────────────────────────

    /// <inheritdoc/>
    public IReadOnlyList<Models.AudioDevice> GetRenderDevices()
    {
        lock (_gate)
        {
            if (_disposed) return [];
            try
            {
                if (!EnsureManager()) return [];
                var devEnum = (IMMDeviceEnumerator)_enumerator!;

                // Default endpoint id (to flag IsDefault) — best-effort.
                string defaultId = "";
                if (devEnum.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var def) == 0 && def is not null)
                {
                    defaultId = EndpointIdOf(def);
                    Release(def);
                }

                // DEVICE_STATE_ACTIVE (0x1) render endpoints.
                if (devEnum.EnumAudioEndpoints(EDataFlow.Render, 0x1, out var collPtr) != 0 || collPtr == IntPtr.Zero)
                    return [];
                var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collPtr);
                try
                {
                    if (collection.GetCount(out int count) != 0) return [];
                    var devices = new List<Models.AudioDevice>(count);
                    for (int i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out var dev) != 0 || dev is null) continue;
                        try
                        {
                            string id = EndpointIdOf(dev);
                            if (id.Length == 0) continue;
                            var (name, formFactor) = ReadEndpointProperties(dev);
                            devices.Add(new Models.AudioDevice(id, name ?? id,
                                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), KindOf(formFactor)));
                        }
                        finally { Release(dev); }
                    }
                    // Default first, then alphabetical.
                    devices.Sort(static (a, b) =>
                    {
                        if (a.IsDefault != b.IsDefault) return a.IsDefault ? -1 : 1;
                        return string.Compare(a.FriendlyName, b.FriendlyName, StringComparison.OrdinalIgnoreCase);
                    });

                    // Windows moved the default since the endpoint was opened (headphones plugged in or pulled out,
                    // the taskbar flyout): drop the old one, so the next pass reads the apps and the PC volume from
                    // the device the sound really goes to. Before, the endpoint was opened once and kept, and the tab
                    // went on listing the first default's apps for the rest of the session (#1588).
                    if (DefaultMoved(_deviceId, defaultId)) ResetManager();
                    return devices;
                }
                finally { Release(collection); Marshal.Release(collPtr); }
            }
            catch (COMException ex)
            {
                Log.Debug("Audio device enumeration failed: {Error}", ex.Message);
                ResetManager();
                return [];
            }
        }
    }

    /// <summary>
    /// Reads a device's friendly name (PKEY_Device_FriendlyName) and form factor (PKEY_AudioEndpoint_FormFactor)
    /// from one opening of its property store. Either is null when the store does not hold it.
    /// </summary>
    private static (string? Name, uint? FormFactor) ReadEndpointProperties(IMMDevice device)
    {
        // STGM_READ = 0.
        if (device.OpenPropertyStore(0, out var store) != 0 || store is null) return (null, null);
        try
        {
            var nameKey = PKEY_Device_FriendlyName;
            string? name = null;
            if (store.GetValue(ref nameKey, out var namePv) == 0)
            {
                try { name = namePv.GetString(); }
                finally { PropVariantClear(ref namePv); }
            }

            var formKey = PKEY_AudioEndpoint_FormFactor;
            uint? formFactor = null;
            if (store.GetValue(ref formKey, out var formPv) == 0)
            {
                try { formFactor = formPv.GetUInt32(); }
                finally { PropVariantClear(ref formPv); }
            }
            return (name, formFactor);
        }
        catch (COMException) { return (null, null); }
        finally { Release(store); }
    }

    /// <summary>
    /// The kind of device an <c>EndpointFormFactor</c> value (mmdeviceapi.h) names. Pure and internal so the
    /// mapping is unit-tested without a device.
    /// </summary>
    internal static AudioDeviceKind KindOf(uint? formFactor) => formFactor switch
    {
        0 => AudioDeviceKind.Network,           // RemoteNetworkDevice
        1 => AudioDeviceKind.Speakers,          // Speakers
        2 => AudioDeviceKind.LineOut,           // LineLevel
        3 => AudioDeviceKind.Headphones,        // Headphones
        5 or 6 => AudioDeviceKind.Headset,      // Headset, Handset
        7 or 8 => AudioDeviceKind.DigitalOutput, // UnknownDigitalPassthrough, SPDIF
        9 => AudioDeviceKind.DisplayAudio,      // DigitalAudioDisplayDevice (HDMI, DisplayPort)
        _ => AudioDeviceKind.Unknown,           // Microphone, UnknownFormFactor, or none reported
    };

    /// <summary>The endpoint id of <paramref name="device"/>, or empty when it cannot be read.</summary>
    private static string EndpointIdOf(IMMDevice device)
    {
        if (device.GetId(out var ptr) != 0 || ptr == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(ptr) ?? ""; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>
    /// Whether the endpoint held open (<paramref name="heldId"/>) is no longer the one Windows plays through. An
    /// empty <paramref name="defaultId"/> — no default at all, the last device unplugged — counts as moved too;
    /// nothing held open means nothing to move. Pure and internal so the decision is unit-tested without a device.
    /// </summary>
    internal static bool DefaultMoved(string? heldId, string defaultId) =>
        heldId is { Length: > 0 } && !string.Equals(heldId, defaultId, StringComparison.OrdinalIgnoreCase);

    // ── Per-app output routing (UNDOCUMENTED IAudioPolicyConfigFactory — guarded) ──

    private bool _routingProbed;
    private bool _routingSupported;

    /// <inheritdoc/>
    public bool IsRoutingSupported
    {
        get { lock (_gate) { return ProbeRoutingLocked(); } }
    }

    /// <summary>
    /// Lazily binds <c>IAudioPolicyConfigFactory</c> once and caches whether it's available. The interface
    /// is undocumented and its class name, IID and vtable are the community-reverse-engineered values; any
    /// failure to activate leaves routing unsupported (the UI then uses the guided fallback).
    /// </summary>
    private bool ProbeRoutingLocked()
    {
        if (_routingProbed) return _routingSupported;
        _routingProbed = true;
        _routingSupported = false;
        try
        {
            _policyConfig = AudioPolicyConfigFactory.TryCreate();
            _routingSupported = _policyConfig is not null;
        }
        catch (COMException ex) { Log.Debug("Audio routing probe failed: {Error}", ex.Message); }
        catch (InvalidCastException ex) { Log.Debug("Audio routing probe cast failed: {Error}", ex.Message); }
        if (!_routingSupported)
            Log.Information("Audio routing: IAudioPolicyConfigFactory unavailable on this build — using guided fallback");
        return _routingSupported;
    }

    private object? _policyConfig; // IAudioPolicyConfigFactory (undocumented)

    /// <inheritdoc/>
    public string? GetSessionOutputDevice(string sessionId)
    {
        lock (_gate)
        {
            // null, not string.Empty, on every path that means "we do not know". Empty is reserved for a
            // successful read that found no override; conflating the two is what made the picker assert the
            // default device for apps it knew nothing about.
            if (_disposed || !ProbeRoutingLocked() || _policyConfig is null) return null;
            if (!_routingKeys.TryGetValue(sessionId, out var key)) return null;
            try
            {
                return AudioPolicyConfigFactory.GetPersistedDefaultEndpoint(_policyConfig, key.Pid);
            }
            catch (COMException ex) { Log.Debug("GetSessionOutputDevice failed: {Error}", ex.Message); return null; }
        }
    }

    /// <inheritdoc/>
    public bool SetSessionOutputDevice(string sessionId, string deviceId)
    {
        lock (_gate)
        {
            if (_disposed || !ProbeRoutingLocked() || _policyConfig is null) return false;
            if (!_routingKeys.TryGetValue(sessionId, out var key)) return false;
            try
            {
                return AudioPolicyConfigFactory.SetPersistedDefaultEndpoint(_policyConfig, key.Pid, deviceId);
            }
            catch (COMException ex) { Log.Debug("SetSessionOutputDevice failed: {Error}", ex.Message); return false; }
        }
    }

    // ── The device all sound plays through (UNDOCUMENTED IPolicyConfig — guarded) ──

    private bool _switchProbed;
    private object? _policyClient; // IPolicyConfig (undocumented)

    /// <inheritdoc/>
    public bool IsDefaultOutputSwitchSupported
    {
        get { lock (_gate) { return !_disposed && ProbeSwitchLocked(); } }
    }

    /// <summary>
    /// Lazily creates the policy-config client once and caches whether it answers, as
    /// <see cref="ProbeRoutingLocked"/> does for routing. When it does not, the This PC card names the current
    /// device and offers Windows' sound settings instead of a picker that would do nothing.
    /// </summary>
    private bool ProbeSwitchLocked()
    {
        if (_switchProbed) return _policyClient is not null;
        _switchProbed = true;
        _policyClient = PolicyConfigClient.TryCreate();
        if (_policyClient is null)
            Log.Information("Default output switch: IPolicyConfig unavailable on this build — using guided fallback");
        return _policyClient is not null;
    }

    /// <inheritdoc/>
    public bool SetDefaultOutputDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        lock (_gate)
        {
            if (_disposed || !ProbeSwitchLocked()) return false;
            var switched = PolicyConfigClient.SetDefaultEndpoint(_policyClient!, deviceId);

            // The endpoint held open is the old default now, or may be: one role can move before another is
            // refused. Drop it either way, so the next read opens the device the sound really goes to and nothing
            // in the meantime moves the old one. Only releasing happens on this (UI) thread; the worker-thread read
            // re-opens.
            ResetManager();
            return switched;
        }
    }

    // ── COM lifetime ──────────────────────────────────────────────────────

    private bool EnsureManager()
    {
        if (_manager is not null) return true;

        var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
        if (enumType is null) return false;

        _enumerator = Activator.CreateInstance(enumType);
        if (_enumerator is not IMMDeviceEnumerator devEnum) { ResetManager(); return false; }

        // eRender + eMultimedia = the endpoint apps render to by default.
        if (devEnum.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0
            || device is null)
        {
            ResetManager();
            return false;
        }
        _device = device;
        _deviceId = EndpointIdOf(device);

        var iid = IID_IAudioSessionManager2;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj) != 0
            || mgrObj is not IAudioSessionManager2)
        {
            ResetManager();
            return false;
        }
        _manager = mgrObj;
        return true;
    }

    private void ResetManager()
    {
        ReleaseGroups();
        Release(_endpointMeter); _endpointMeter = null;
        Release(_endpointVolume); _endpointVolume = null;
        _pcPeakFailureLogged = false;
        Release(_manager); _manager = null;
        Release(_device); _device = null;
        _deviceId = null;
        Release(_enumerator); _enumerator = null;
        // Routing keys reference the (now-stale) enumeration; drop them. The two policy-config RCWs (routing and
        // the default switch) are independent of the endpoint handle, so they survive a manager reset.
        _routingKeys.Clear();
    }

    private void ReleaseGroups()
    {
        foreach (var controls in _groups.Values)
            foreach (var control in controls)
                Release(control);
        _groups.Clear();
        // New handles mean a new chance to succeed — and a failure against them is new information,
        // so allow it to be logged once more rather than staying silent for the whole session.
        _peakFailureLogged = false;
    }

    private static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject)) return;
        try { Marshal.ReleaseComObject(comObject); }
        catch (ArgumentException) { /* not an RCW — nothing to release */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ResetManager();
            Release(_policyConfig); _policyConfig = null;
            Release(_policyClient); _policyClient = null;
        }
    }

    // (name, path) cache keyed by the stable GROUP KEY — NOT the recyclable PID. Keying on the
    // group key (session-instance-derived) means a reused PID that opens a new session gets a new
    // key and therefore misses the cache and re-resolves, so a row can never show a prior process's
    // stale name/icon. Bounded so a long session with many short-lived audio apps can't grow it
    // without limit. The slow MainModule walk is thus paid once per app-session-group, not per tick.
    private readonly Dictionary<string, (string Name, string Path)> _identityCache = new(StringComparer.Ordinal);
    private const int IdentityCacheMax = 256;

    private (string Name, string Path) ResolveIdentityCached(string groupKey, uint pid, bool pidKnown)
    {
        if (_identityCache.TryGetValue(groupKey, out var cached)) return cached;

        var resolved = ResolveProcess(pid, pidKnown);
        if (_identityCache.Count >= IdentityCacheMax) _identityCache.Clear();
        _identityCache[groupKey] = resolved;
        return resolved;
    }

    private static (string Name, string Path) ResolveProcess(uint pid, bool pidKnown)
    {
        // A non-system session whose PID couldn't be read (GetProcessId failed → pid 0) must not
        // be mislabeled "System Sounds" (that name is reserved for the real system-sounds session,
        // routed via GroupAccumulator.IsSystemSounds). Give it a neutral label instead.
        if (!pidKnown || pid == 0) return ("Unknown app", string.Empty);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string name = p.ProcessName;
            string path = string.Empty;
            try
            {
                var module = p.MainModule;
                path = module?.FileName ?? string.Empty;
                var description = module?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) name = description!;
            }
            // MainModule throws for protected / cross-bitness processes when not elevated —
            // fall back to the process name and no path (icon degrades to the fallback).
            catch (Win32Exception) { }
            catch (InvalidOperationException) { }
            return (name, path);
        }
        catch (ArgumentException) { return ($"PID {pid}", string.Empty); }   // exited mid-enumeration
        catch (InvalidOperationException) { return ($"PID {pid}", string.Empty); }
    }

    /// <summary>
    /// Mutable per-app aggregate built while walking the flat session list under the COM gate.
    /// Holds only the fast COM values; the display name/path are supplied later by
    /// <see cref="ToInfo"/> once identity is resolved outside the lock.
    /// </summary>
    private sealed class GroupAccumulator(string key, uint pid, bool pidKnown, bool isSystemSounds)
    {
        private bool _first = true;
        private float _volume;
        private bool _muted;
        private AudioSessionState _state = AudioSessionState.Inactive;
        private float _peak;

        public string GroupKey => key;
        public uint ProcessId => pid;
        public bool PidKnown => pidKnown;
        public bool IsSystemSounds => isSystemSounds;

        public void Absorb(float volume, bool muted, AudioSessionState state, float peak)
        {
            // The first session in the group defines the representative volume/mute the
            // slider shows; every session gets the write on set (see SetVolume/SetMute).
            if (_first) { _volume = volume; _muted = muted; _first = false; }
            if (state == AudioSessionState.Active) _state = AudioSessionState.Active;
            if (peak > _peak) _peak = peak;
        }

        public AudioSessionInfo ToInfo(string name, string path) =>
            new(key, pid, name, path, _volume, _muted, _state, isSystemSounds, _peak);
    }

    // ── Core Audio COM interop (documented interfaces, exact vtable order) ──

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const uint CLSCTX_ALL = 0x17;

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        // Remaining members (GetDevice / register callbacks) are unused — the vtable slots
        // above are all that's needed, so they are intentionally not declared.
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        // Slots 2-4, declared to preserve vtable order (device enumeration reads name + id).
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId(out IntPtr ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int pcDevices);
        [PreserveSig] int Item(int nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant propvar);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    // PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14.
    private static PropertyKey PKEY_Device_FriendlyName => new()
    {
        FmtId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        Pid = 14
    };

    // PKEY_AudioEndpoint_FormFactor = {1da5d803-d492-4edd-8c23-e0c0ffee7f0e}, 0 — a VT_UI4 EndpointFormFactor.
    private static PropertyKey PKEY_AudioEndpoint_FormFactor => new()
    {
        FmtId = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"),
        Pid = 0
    };

    // Minimal PROPVARIANT: we only ever read a VT_LPWSTR (the friendly name) or a VT_UI4 (the form factor). The
    // union is represented by the pointer-sized field at offset 8 (x64): a wide-string pointer for VT_LPWSTR, and
    // a 32-bit value in its low half for VT_UI4.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p; // union: for VT_LPWSTR this is the wide-string pointer
        public IntPtr p2;

        public readonly string? GetString() => vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(p) : null;

        public readonly uint? GetUInt32() => vt == 19 /* VT_UI4 */ ? unchecked((uint)p.ToInt64()) : null;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // Inherited from IAudioSessionManager (slots 1-2) — declared to preserve vtable order.
        [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr audioSessionGuid, int crossProcessSession, out IntPtr audioVolume);
        // IAudioSessionManager2 addition (slot 3).
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        // Notification registration members are unused — not declared.
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // Base IAudioSessionControl (slots 1-9).
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr retVal);
        [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr retVal);
        [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid retVal);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
        // IAudioSessionControl2 additions (slots 10-13).
        [PreserveSig] int GetSessionIdentifier(out IntPtr retVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        [PreserveSig] int GetProcessId(out uint retVal);
        [PreserveSig] int IsSystemSoundsSession(); // S_OK (0) == true, S_FALSE (1) == false
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        // Channel-count / channel-peaks / hardware-support members are unused — not declared.
    }

    // The default endpoint's own volume (endpointvolume.h), in vtable order. The PC volume reads and writes the
    // scalar level and the mute; the members between them are declared only to keep the order.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        // Step, hardware-support and range members are unused — not declared.
    }
}
