// SysManager · GpuVramHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;

namespace SysManager.Helpers;

/// <summary>
/// Single source of truth for resolving a GPU's true VRAM size.
/// <c>Win32_VideoController.AdapterRAM</c> is a <c>uint32</c> (CIM_UINT32) that saturates near
/// 4 GiB, so on its own it mis-reports every modern &gt;4 GB card. The full size is recorded in
/// the driver's registry key as a 64-bit <c>qwMemorySize</c>; this helper prefers that and falls
/// back to <c>AdapterRAM</c> only when the registry value is missing/zero. Both the System Report
/// and the About-page diagnostics route through here so the two never drift apart.
/// </summary>
public static class GpuVramHelper
{
    // The display-adapter class node; each adapter is a numbered subkey (0000, 0001, …).
    private const string ClassRoot =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Resolves VRAM in gigabytes from the two WMI fields, or null when neither is usable.
    /// </summary>
    public static double? ResolveVramGB(string? pnpDeviceId, ulong? adapterRam)
        => SelectVramBytes(ReadQwMemorySize(pnpDeviceId), adapterRam) is { } bytes
            ? bytes / 1024.0 / 1024.0 / 1024.0
            : null;

    /// <summary>
    /// Chooses the VRAM byte count: the driver's 64-bit <c>qwMemorySize</c> when present and
    /// non-zero, otherwise the WMI <c>AdapterRAM</c> (uint32-capped) as a fallback. Returns null
    /// when neither is a usable positive value. Pure so it is unit-testable.
    /// </summary>
    public static ulong? SelectVramBytes(ulong? qwMemorySize, ulong? adapterRam)
    {
        if (qwMemorySize is > 0) return qwMemorySize;
        if (adapterRam is > 0) return adapterRam;
        return null;
    }

    /// <summary>
    /// Reads the true VRAM size (bytes) from the GPU's driver registry key. Windows records it as
    /// a REG_QWORD <c>HardwareInformation.qwMemorySize</c> under the adapter's class node, matched
    /// to this controller by a <c>MatchingDeviceId</c> that is a prefix of the WMI
    /// <c>PNPDeviceID</c>. Returns null when the key/value is absent or unreadable — the caller
    /// then falls back to AdapterRAM.
    /// </summary>
    public static ulong? ReadQwMemorySize(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return null;
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(ClassRoot);
            if (classKey is null) return null;

            foreach (var sub in classKey.GetSubKeyNames())
            {
                using var adapter = classKey.OpenSubKey(sub);
                if (adapter is null) continue;
                if (adapter.GetValue("MatchingDeviceId") is not string match) continue;
                if (!pnpDeviceId.StartsWith(match, StringComparison.OrdinalIgnoreCase)) continue;

                if (adapter.GetValue("HardwareInformation.qwMemorySize") is { } qw)
                {
                    var bytes = Convert.ToUInt64(qw);
                    return bytes > 0 ? bytes : null;
                }
                return null;
            }
        }
        catch (System.Security.SecurityException) { /* registry ACL — fall back to AdapterRAM */ }
        catch (UnauthorizedAccessException) { /* registry ACL — fall back to AdapterRAM */ }
        catch (IOException) { /* transient registry error — fall back to AdapterRAM */ }
        catch (FormatException) { /* unexpected value type — fall back to AdapterRAM */ }
        catch (InvalidCastException) { /* unexpected value type — fall back to AdapterRAM */ }
        return null;
    }
}
