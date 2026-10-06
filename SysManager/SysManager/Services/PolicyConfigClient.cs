// SysManager · PolicyConfigClient — UNDOCUMENTED default output switch (guarded, feature-detected)
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// Thin, defensive wrapper over the <b>undocumented</b> Windows <c>IPolicyConfig</c> interface — the only way
/// for an app to change the device all sound plays through, which is what the Windows sound flyout does when you
/// pick a device (#1588). Windows documents how to READ the default device, and SysManager reads it that way;
/// only the switch goes through here. Because the interface is undocumented:
/// <list type="bullet">
/// <item>The CLSID, IID and method order are the long-published ones (the same EarTrumpet and other sound
/// switchers use), stable since Windows 7. The IID names one immutable vtable, so a successful
/// <c>QueryInterface</c> for it is what makes the call safe rather than a blind offset.</item>
/// <item>Everything is feature-detected and guarded: <see cref="TryCreate"/> returns null when the object or
/// the interface is not there, and <see cref="SetDefaultEndpoint"/> catches COM failures and returns false.
/// Callers treat null as "name the current device and guide the user to Windows' sound settings".</item>
/// </list>
/// <para>Checked on Windows 11 build 26200: the class answers to this IID, and its first slot
/// (<c>GetMixFormat</c>, a read) returns the default device's mix format.</para>
/// </summary>
internal static class PolicyConfigClient
{
    // CPolicyConfigClient, in AudioSes.dll.
    private static readonly Guid CLSID_PolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    /// <summary>
    /// The three defaults Windows keeps — everyday, multimedia, and calls. "Sound plays through" means all of
    /// them, which is also what the Windows sound flyout switches.
    /// </summary>
    private static readonly ERole[] Roles = [ERole.Console, ERole.Multimedia, ERole.Communications];

    /// <summary>
    /// Attempts to create the client and confirms it answers to <c>IPolicyConfig</c>. Returns the RCW (boxed in
    /// <see cref="object"/> so no undocumented type leaks to callers) on success, else null. Never throws.
    /// </summary>
    public static object? TryCreate()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient, throwOnError: false);
            if (type is null) return null;
            var client = Activator.CreateInstance(type);
            if (client is IPolicyConfig) return client;

            if (client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client);
            return null;
        }
        catch (COMException ex) { Log.Debug("PolicyConfigClient activate failed: {Error}", ex.Message); return null; }
        catch (InvalidCastException ex) { Log.Debug("PolicyConfigClient cast failed: {Error}", ex.Message); return null; }
        catch (NotSupportedException ex) { Log.Debug("PolicyConfigClient not supported: {Error}", ex.Message); return null; }
    }

    /// <summary>
    /// Makes <paramref name="endpointId"/> (a plain Core Audio endpoint id, as <c>IMMDevice::GetId</c> returns it)
    /// the default for every role. Each role is attempted even when an earlier one fails, so a partial refusal
    /// leaves as much as possible where the user asked; true only when Windows applied all three. Never throws.
    /// </summary>
    public static bool SetDefaultEndpoint(object client, string endpointId)
    {
        if (client is not IPolicyConfig config || string.IsNullOrEmpty(endpointId)) return false;
        try
        {
            var all = true;
            foreach (var role in Roles)
                all &= config.SetDefaultEndpoint(endpointId, role) >= 0;
            return all;
        }
        catch (COMException ex) { Log.Debug("SetDefaultEndpoint failed: {Error}", ex.Message); return false; }
        catch (ArgumentException ex) { Log.Debug("SetDefaultEndpoint arg failed: {Error}", ex.Message); return false; }
    }

    /// <summary>
    /// The undocumented <c>IPolicyConfig</c>. Only the method SysManager calls carries a real signature; the ten
    /// slots ahead of it are declared as no-argument <c>[PreserveSig]</c> HRESULT placeholders purely to keep the
    /// vtable order, and are never called.
    /// </summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int Slot00_GetMixFormat();
        [PreserveSig] int Slot01_GetDeviceFormat();
        [PreserveSig] int Slot02_ResetDeviceFormat();
        [PreserveSig] int Slot03_SetDeviceFormat();
        [PreserveSig] int Slot04_GetProcessingPeriod();
        [PreserveSig] int Slot05_SetProcessingPeriod();
        [PreserveSig] int Slot06_GetShareMode();
        [PreserveSig] int Slot07_SetShareMode();
        [PreserveSig] int Slot08_GetPropertyValue();
        [PreserveSig] int Slot09_SetPropertyValue();

        // Slot 10: the one we call.
        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    }
}
