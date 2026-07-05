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
/// group's volume, mute, and VU peak. Uses raw <c>[ComImport]</c> interop for the six
/// documented interfaces (<c>IMMDeviceEnumerator</c> → <c>IAudioSessionManager2</c> →
/// <c>IAudioSessionEnumerator</c> → <c>IAudioSessionControl(2)</c> /
/// <c>ISimpleAudioVolume</c> / <c>IAudioMeterInformation</c>) so nothing but the .NET
/// runtime is added to the single portable .exe.
///
/// <para>Scope: default render endpoint only (Windows' own mixer is per-device too);
/// sessions on other output devices are not shown. Per-app <em>output-device routing</em>
/// and volume <em>presets</em> are intentionally out of scope for this preview — the only
/// public path to routing is an undocumented, version-fragile internal WinRT class.</para>
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
    private bool _disposed;

    // Per-app cached session interfaces, keyed by the group key (PID string, or the
    // "system-sounds" sentinel). Each app can own several render sessions; the group holds
    // all of their control RCWs so a single slider/mute drives every stream of the app.
    private readonly Dictionary<string, List<object>> _groups = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public IReadOnlyList<AudioSessionInfo> GetSessions()
    {
        lock (_gate)
        {
            if (_disposed) return [];

            var results = new List<AudioSessionInfo>();
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

                    // Accumulate per-group data as we walk the (unordered) session list.
                    var acc = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);

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
                        uint pid = 0;
                        ctl2.GetProcessId(out pid);

                        string key = isSystemSounds ? "system-sounds" : pid.ToString();

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
                            var (name, path) = isSystemSounds
                                ? ("System Sounds", string.Empty)
                                : ResolveProcess(pid);
                            group = new GroupAccumulator(key, pid, name, path, isSystemSounds);
                            acc[key] = group;
                            _groups[key] = [];
                        }

                        _groups[key].Add(control);          // cache the RCW for set/get later
                        group.Absorb(volume, muted, state, peak);
                    }

                    foreach (var g in acc.Values)
                        results.Add(g.ToInfo());
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

            // Stable order so the reconcile diff is deterministic: system sounds last,
            // apps alphabetical.
            results.Sort(static (a, b) =>
            {
                if (a.IsSystemSounds != b.IsSystemSounds) return a.IsSystemSounds ? 1 : -1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return results;
        }
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
            if (_disposed || !_groups.TryGetValue(sessionId, out var controls)) return 0f;

            float peak = 0f;
            foreach (var control in controls)
            {
                if (control is not IAudioMeterInformation meter) continue;
                try
                {
                    if (meter.GetPeakValue(out float value) == 0 && value > peak) peak = value;
                }
                catch (COMException ex) { Log.Debug("GetPeak failed: {Error}", ex.Message); }
            }
            return peak;
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
        Release(_manager); _manager = null;
        Release(_device); _device = null;
        Release(_enumerator); _enumerator = null;
    }

    private void ReleaseGroups()
    {
        foreach (var controls in _groups.Values)
            foreach (var control in controls)
                Release(control);
        _groups.Clear();
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
        }
    }

    private static (string Name, string Path) ResolveProcess(uint pid)
    {
        if (pid == 0) return ("System Sounds", string.Empty);
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

    /// <summary>Mutable per-app aggregate built while walking the flat session list.</summary>
    private sealed class GroupAccumulator(string key, uint pid, string name, string path, bool isSystemSounds)
    {
        private bool _first = true;
        private float _volume;
        private bool _muted;
        private AudioSessionState _state = AudioSessionState.Inactive;
        private float _peak;

        public void Absorb(float volume, bool muted, AudioSessionState state, float peak)
        {
            // The first session in the group defines the representative volume/mute the
            // slider shows; every session gets the write on set (see SetVolume/SetMute).
            if (_first) { _volume = volume; _muted = muted; _first = false; }
            if (state == AudioSessionState.Active) _state = AudioSessionState.Active;
            if (peak > _peak) _peak = peak;
        }

        public AudioSessionInfo ToInfo() =>
            new(key, pid, name, path, _volume, _muted, _state, isSystemSounds, _peak);
    }

    // ── Core Audio COM interop (documented interfaces, exact vtable order) ──

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
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
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        // OpenPropertyStore / GetId / GetState are unused — not declared.
    }

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
}
