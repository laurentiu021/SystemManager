// SysManager · AudioPolicyConfig — UNDOCUMENTED per-app audio routing (guarded, feature-detected)
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// Thin, defensive wrapper over the <b>undocumented</b> Windows <c>IAudioPolicyConfigFactory</c> interface
/// — the only mechanism that lets an app set another app's default output endpoint (what EarTrumpet
/// uses for per-app routing). Because it is undocumented:
/// <list type="bullet">
/// <item>The interface lives on the activation factory of the Windows Runtime class
/// <c>Windows.Media.Internal.AudioPolicyConfig</c>, reached through <c>RoGetActivationFactory</c>. Routing never
/// turned on before #2584 because it was asked of the COM class <c>870af99c-…</c>, the policy-config client, which
/// answers only to the default-device interface (see <see cref="PolicyConfigClient"/>).</item>
/// <item>The IID and method layout are the community-reverse-engineered values (the same ones the MIT-licensed
/// EarTrumpet ships). Windows 10 21H2 and later, and Windows 11, expose it as <c>ab3d4648-…</c>. Windows 10 before
/// 21H2 used <c>2a59116d-…</c>; those builds are out of support, and there routing stays off.</item>
/// <item>Everything is feature-detected and guarded: <see cref="TryCreate"/> returns null if the factory can't be
/// reached or bound, the write returns false and the read returns null on any failure. Callers treat that as "fall
/// back to guiding the user to Windows sound settings" or "the route is unknown", so a build where this shape
/// changed degrades gracefully.</item>
/// </list>
/// <para>Confirmed on Windows 11 build 26200 with two output devices: a route set for a process reads back as the same
/// endpoint, a new process from the same program reads it too, and clearing it reads back as empty.</para>
/// </summary>
internal static partial class AudioPolicyConfigFactory
{
    // The Windows Runtime class whose activation factory is the routing interface.
    private const string ActivatableClassId = "Windows.Media.Internal.AudioPolicyConfig";

    // IAudioPolicyConfigFactory as Windows 10 21H2 and later, and Windows 11, expose it.
    private static readonly Guid IID_IAudioPolicyConfigFactory = new("ab3d4648-e242-459f-b02f-541c70306324");

    // Endpoint-string wrappers IAudioPolicyConfigFactory expects around a plain Core Audio endpoint id.
    private const string MMDeviceApiTokenPrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceGuid = "{e6327cad-dcec-4949-ae8a-991e976a79d2}"; // DEVINTERFACE_AUDIO_RENDER

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    /// <summary>
    /// Attempts to bind the routing interface. Returns the RCW (typed as the interface, boxed in
    /// <see cref="object"/> so no undocumented type leaks to callers) on success, else null. Never throws.
    /// </summary>
    public static object? TryCreate()
    {
        var className = IntPtr.Zero;
        try
        {
            if (WindowsCreateString(ActivatableClassId, ActivatableClassId.Length, out className) < 0) return null;

            var hr = RoGetActivationFactory(className, in IID_IAudioPolicyConfigFactory, out var factory);
            if (hr < 0 || factory == IntPtr.Zero)
            {
                Log.Debug("AudioPolicyConfig factory unavailable: 0x{Result:X8}", hr);
                return null;
            }

            try
            {
                // The pointer RoGetActivationFactory returned is already this exact interface, so the cast's
                // QueryInterface asks the object for the interface it was just handed out as.
                return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(factory);
            }
            finally { Marshal.Release(factory); }
        }
        catch (COMException ex) { Log.Debug("AudioPolicyConfig activate failed: {Error}", ex.Message); return null; }
        catch (InvalidCastException ex) { Log.Debug("AudioPolicyConfig cast failed: {Error}", ex.Message); return null; }
        catch (EntryPointNotFoundException ex) { Log.Debug("AudioPolicyConfig not supported: {Error}", ex.Message); return null; }
        catch (DllNotFoundException ex) { Log.Debug("AudioPolicyConfig not supported: {Error}", ex.Message); return null; }
        finally
        {
            if (className != IntPtr.Zero) WindowsDeleteString(className);
        }
    }

    /// <summary>
    /// The render device <paramref name="processId"/>'s program is routed to, as a plain Core Audio endpoint id
    /// (#2088). <see cref="string.Empty"/> when the read succeeded and the program follows the Windows default, null
    /// when the route could not be read. Never throws.
    /// <para>The three states are the contract <c>IAudioMixerService.GetSessionOutputDevice</c> passes on: the row
    /// shows null as unknown ("Choose a device") rather than as the default device, and empty as the entry flagged
    /// <c>IsDefault</c>. The empty id is the pivot in both directions: selecting that entry sends empty back through
    /// <see cref="SetPersistedDefaultEndpoint"/>, which CLEARS the override.</para>
    /// <para>Windows keeps the route per program rather than per process, so a route set before SysManager or the app
    /// restarted still reads back. Only the Multimedia role is read: the write sets it and Console together.</para>
    /// </summary>
    public static string? GetPersistedDefaultEndpoint(object policyConfig, uint processId)
    {
        if (policyConfig is not IAudioPolicyConfigFactory cfg) return null;
        var route = IntPtr.Zero;
        try
        {
            var hr = cfg.GetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Multimedia, out route);
            if (hr < 0)
            {
                Log.Debug("GetPersistedDefaultAudioEndpoint failed: 0x{Result:X8}", hr);
                return null;
            }

            return FromPolicyEndpointId(TextOf(route));
        }
        catch (COMException ex) { Log.Debug("GetPersistedDefaultAudioEndpoint failed: {Error}", ex.Message); return null; }
        finally
        {
            if (route != IntPtr.Zero) WindowsDeleteString(route);
        }
    }

    /// <summary>
    /// Sets the persisted default render endpoint for a process (empty <paramref name="endpointId"/>
    /// clears the override → follow system default), for both the Multimedia and Console roles (what
    /// EarTrumpet does so both "media" and "communication" default to the chosen device). Returns true
    /// only if the COM calls succeeded. Never throws.
    /// </summary>
    public static bool SetPersistedDefaultEndpoint(object policyConfig, uint processId, string endpointId)
    {
        if (policyConfig is not IAudioPolicyConfigFactory cfg) return false;
        var device = string.IsNullOrEmpty(endpointId) ? string.Empty : ToPolicyEndpointId(endpointId);

        // An empty route is the null HSTRING, which is what clears the override.
        var route = IntPtr.Zero;
        try
        {
            if (device.Length > 0 && WindowsCreateString(device, device.Length, out route) < 0) return false;

            // Apply to Multimedia AND Console roles for the render flow (Communications is left to the
            // system so a headset-switch doesn't hijack call audio unexpectedly).
            int hr1 = cfg.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Multimedia, route);
            int hr2 = cfg.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Console, route);
            return hr1 >= 0 && hr2 >= 0;
        }
        catch (COMException ex) { Log.Debug("SetPersistedDefaultAudioEndpoint failed: {Error}", ex.Message); return false; }
        finally
        {
            if (route != IntPtr.Zero) WindowsDeleteString(route);
        }
    }

    /// <summary>
    /// Wraps a plain Core Audio endpoint id in the <c>\\?\SWD#MMDEVAPI#…#{render-iface-guid}</c> form
    /// <c>IAudioPolicyConfigFactory</c> persists. Pass-through if it already carries the SWD prefix. Pure and
    /// unit-tested (the format is easy to get subtly wrong, so it is pinned by a test).
    /// </summary>
    internal static string ToPolicyEndpointId(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return string.Empty;
        if (endpointId.StartsWith(MMDeviceApiTokenPrefix, StringComparison.OrdinalIgnoreCase)) return endpointId;
        return $"{MMDeviceApiTokenPrefix}{endpointId}#{RenderInterfaceGuid}";
    }

    /// <summary>
    /// The plain Core Audio endpoint id inside a persisted route, the inverse of <see cref="ToPolicyEndpointId"/>, so
    /// it can be matched against the device list. Empty stays empty: the program follows the default. Pure and
    /// unit-tested.
    /// </summary>
    internal static string FromPolicyEndpointId(string policyEndpointId)
    {
        var id = policyEndpointId;
        if (id.StartsWith(MMDeviceApiTokenPrefix, StringComparison.OrdinalIgnoreCase)) id = id[MMDeviceApiTokenPrefix.Length..];
        var suffix = "#" + RenderInterfaceGuid;
        if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) id = id[..^suffix.Length];
        return id;
    }

    // Retained for tests/diagnostics: the process token IAudioPolicyConfigFactory keys on is the raw PID.
    internal static string BuildProcessToken(uint processId) => processId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The text an <c>HSTRING</c> holds. A null handle is the empty string.</summary>
    private static string TextOf(IntPtr handle)
    {
        var chars = WindowsGetStringRawBuffer(handle, out var length);
        return chars == IntPtr.Zero || length == 0 ? string.Empty : Marshal.PtrToStringUni(chars, (int)length);
    }

    /// <summary>
    /// <c>RoGetActivationFactory</c> and the Windows Runtime string calls have no A/W variants, so no <c>EntryPoint</c>
    /// suffix applies. Each reports failure as an HRESULT return.
    /// </summary>
    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, int length, out IntPtr handle);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(IntPtr handle);

    [LibraryImport("combase.dll")]
    private static partial IntPtr WindowsGetStringRawBuffer(IntPtr handle, out uint length);

    /// <summary>
    /// The undocumented <c>IAudioPolicyConfigFactory</c>, a Windows Runtime interface. It derives from
    /// <c>IInspectable</c>, which .NET's built-in COM interop no longer projects, so it is declared on <c>IUnknown</c>
    /// with <c>IInspectable</c>'s three methods as its first slots. The 19 slots after them are never called and are
    /// declared only to keep the vtable in order; the two methods SysManager calls take the device id as an
    /// <c>HSTRING</c> handle. Layout mirrors EarTrumpet's definition.
    /// </summary>
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactory
    {
        [PreserveSig] int GetIids();
        [PreserveSig] int GetRuntimeClassName();
        [PreserveSig] int GetTrustLevel();

        [PreserveSig] int Slot00_add_CtxVolumeChange();
        [PreserveSig] int Slot01_remove_CtxVolumeChanged();
        [PreserveSig] int Slot02_add_RingerVibrateStateChanged();
        [PreserveSig] int Slot03_remove_RingerVibrateStateChanged();
        [PreserveSig] int Slot04_SetVolumeGroupGainForId();
        [PreserveSig] int Slot05_GetVolumeGroupGainForId();
        [PreserveSig] int Slot06_GetActiveVolumeGroupForEndpointId();
        [PreserveSig] int Slot07_GetVolumeGroupsForEndpoint();
        [PreserveSig] int Slot08_GetCurrentVolumeContext();
        [PreserveSig] int Slot09_SetVolumeGroupMuteForId();
        [PreserveSig] int Slot10_GetVolumeGroupMuteForId();
        [PreserveSig] int Slot11_SetRingerVibrateState();
        [PreserveSig] int Slot12_GetRingerVibrateState();
        [PreserveSig] int Slot13_SetPreferredChatApplication();
        [PreserveSig] int Slot14_ResetPreferredChatApplication();
        [PreserveSig] int Slot15_GetPreferredChatApplication();
        [PreserveSig] int Slot16_GetCurrentChatApplications();
        [PreserveSig] int Slot17_add_ChatContextChanged();
        [PreserveSig] int Slot18_remove_ChatContextChanged();

        // A null deviceId clears the program's override, so it follows the Windows default again.
        [PreserveSig]
        int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

        // deviceId comes back as an HSTRING the caller deletes; a null one means no override.
        [PreserveSig]
        int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
    }
}
