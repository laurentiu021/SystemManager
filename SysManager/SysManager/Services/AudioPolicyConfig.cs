// SysManager · AudioPolicyConfig — UNDOCUMENTED per-app audio routing (guarded, feature-detected)
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// Thin, defensive wrapper over the <b>undocumented</b> Windows <c>IAudioPolicyConfig</c> interface
/// — the only mechanism that lets an app set another app's default output endpoint (what EarTrumpet
/// uses for per-app routing). Because it is undocumented:
/// <list type="bullet">
/// <item>The CLSID/IID and method layout are the community-reverse-engineered values (the same ones
/// the MIT-licensed EarTrumpet ships). Two IIDs are tried — Windows 10 then Windows 11 — over one
/// method layout that is identical across both in the modern definitions.</item>
/// <item>Everything is feature-detected and guarded: <see cref="TryCreate"/> returns null if the
/// interface can't be activated/queried, and <see cref="SetPersistedDefaultEndpoint"/> catches COM
/// failures and returns false. Callers treat "unavailable/false" as "fall back to guiding the user
/// to Windows sound settings", so a build where this shape changed degrades gracefully.</item>
/// </list>
/// <para>Safety note: the SET call goes through a <c>[ComImport]</c> interface (CLR-marshaled, not a
/// raw vtable pointer), invoked only after a successful <c>QueryInterface</c> for the exact IID — by
/// COM's contract the returned object's vtable then matches that interface, which is what makes the
/// call safe rather than a blind offset. The route-read is intentionally not attempted (returning
/// "system default") because its out-string marshaling is the most build-variant part.</para>
/// </summary>
internal static class AudioPolicyConfigFactory
{
    // MMDevAPI CAudioPolicyConfigFactory CLSID (stable across builds).
    private static readonly Guid CLSID_AudioPolicyConfigFactory = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    // Endpoint-string wrappers IAudioPolicyConfig expects around a plain Core Audio endpoint id.
    private const string MMDeviceApiTokenPrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceGuid = "{e6327cad-dcec-4949-ae8a-991e976a79d2}"; // DEVINTERFACE_AUDIO_RENDER

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    /// <summary>
    /// Attempts to activate <c>IAudioPolicyConfig</c>. Returns the RCW (typed as the interface, boxed
    /// in <see cref="object"/> so no undocumented type leaks to callers) on success, else null. Never
    /// throws.
    /// </summary>
    public static object? TryCreate()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_AudioPolicyConfigFactory, throwOnError: false);
            if (type is null) return null;
            var factory = Activator.CreateInstance(type);
            if (factory is null) return null;

            // The interface is declared with the Win11 IID; a direct cast QIs for it. If that fails
            // (older build exposing only the Win10 IID over the same layout), QI manually for the
            // Win10 IID and reinterpret the pointer as the same managed interface type.
            if (factory is IAudioPolicyConfig direct) return direct;

            var unk = Marshal.GetIUnknownForObject(factory);
            try
            {
                var win10 = new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
                if (Marshal.QueryInterface(unk, in win10, out var ppv) == 0 && ppv != IntPtr.Zero)
                {
                    try
                    {
                        // Same modern method layout as the Win11 IID → safe to project onto it.
                        return Marshal.GetTypedObjectForIUnknown(ppv, typeof(IAudioPolicyConfig));
                    }
                    finally { Marshal.Release(ppv); }
                }
            }
            finally { Marshal.Release(unk); }

            if (Marshal.IsComObject(factory)) { try { Marshal.ReleaseComObject(factory); } catch (ArgumentException) { } }
            return null;
        }
        catch (COMException ex) { Log.Debug("AudioPolicyConfig activate failed: {Error}", ex.Message); return null; }
        catch (InvalidCastException ex) { Log.Debug("AudioPolicyConfig cast failed: {Error}", ex.Message); return null; }
        catch (NotSupportedException ex) { Log.Debug("AudioPolicyConfig not supported: {Error}", ex.Message); return null; }
    }

    /// <summary>
    /// Route-read is intentionally not attempted (its out-string marshaling is the most build-variant
    /// slot); the UI shows "System default" and lets the user pick. Returns null. Kept as the seam so
    /// a future, verified read path can slot in without changing callers.
    /// </summary>
    public static string? GetPersistedDefaultEndpoint(object policyConfig, uint processId)
    {
        _ = policyConfig;
        _ = processId;
        return null;
    }

    /// <summary>
    /// Sets the persisted default render endpoint for a process (empty <paramref name="endpointId"/>
    /// clears the override → follow system default), for both the Multimedia and Console roles (what
    /// EarTrumpet does so both "media" and "communication" default to the chosen device). Returns true
    /// only if the COM calls succeeded. Never throws.
    /// </summary>
    public static bool SetPersistedDefaultEndpoint(object policyConfig, uint processId, string endpointId)
    {
        if (policyConfig is not IAudioPolicyConfig cfg) return false;
        string deviceString = string.IsNullOrEmpty(endpointId) ? string.Empty : ToPolicyEndpointId(endpointId);
        try
        {
            // Apply to Multimedia AND Console roles for the render flow (Communications is left to the
            // system so a headset-switch doesn't hijack call audio unexpectedly).
            int hr1 = cfg.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Multimedia, deviceString);
            int hr2 = cfg.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Console, deviceString);
            return hr1 >= 0 && hr2 >= 0;
        }
        catch (COMException ex) { Log.Debug("SetPersistedDefaultAudioEndpoint failed: {Error}", ex.Message); return false; }
        catch (ArgumentException ex) { Log.Debug("SetPersistedDefaultAudioEndpoint arg failed: {Error}", ex.Message); return false; }
    }

    /// <summary>
    /// Wraps a plain Core Audio endpoint id in the <c>\\?\SWD#MMDEVAPI#…#{render-iface-guid}</c> form
    /// <c>IAudioPolicyConfig</c> persists. Pass-through if it already carries the SWD prefix. Pure and
    /// unit-tested (the format is easy to get subtly wrong, so it is pinned by a test).
    /// </summary>
    internal static string ToPolicyEndpointId(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return string.Empty;
        if (endpointId.StartsWith(MMDeviceApiTokenPrefix, StringComparison.OrdinalIgnoreCase)) return endpointId;
        return $"{MMDeviceApiTokenPrefix}{endpointId}#{RenderInterfaceGuid}";
    }

    // Retained for tests/diagnostics: the process token IAudioPolicyConfig keys on is the raw PID.
    internal static string BuildProcessToken(uint processId) => processId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The undocumented <c>IAudioPolicyConfig</c> (Windows 11 IID; the Windows 10 IID shares this
    /// modern method layout). Only the two methods SysManager needs carry real signatures; the 19
    /// preceding vtable slots are declared as no-arg <c>[PreserveSig]</c> HRESULT placeholders purely
    /// to preserve vtable order — they are never called. Layout mirrors EarTrumpet's definition.
    /// </summary>
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfig
    {
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

        // Slot 19: the one we call. deviceId is a plain LPWStr (the EarTrumpet-observed marshaling on
        // Win10 1803+ and Win11); an empty string clears the per-app override.
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(
            uint processId, EDataFlow flow, ERole role,
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    }
}
