// SysManager · DisplayProfileService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads and switches display resolution + refresh rate via the Windows display
/// APIs (EnumDisplayDevices / EnumDisplaySettings / ChangeDisplaySettingsEx). Uses
/// only the OS APIs — no NVIDIA/AMD SDK — so it never conflicts with vendor tools.
///
/// Applying a mode for the session (dwflags = 0, not CDS_UPDATEREGISTRY) is reversible
/// by design: a logoff/reboot restores the previous mode, and the ViewModel keeps a
/// timed auto-revert in case a bad mode blanks the panel. No admin required.
///
/// NOTE on interop style: classic <c>[DllImport]</c> with <c>CharSet.Unicode</c> and
/// explicit <c>EntryPoint="...W"</c> rather than <c>[LibraryImport]</c> — DEVMODE and
/// DISPLAY_DEVICE contain inline <c>ByValTStr</c> buffers (non-blittable), unsupported
/// by the source generator (SYSLIB1051). These functions have A/W variants, so the W
/// entry point is named explicitly.
/// </summary>
public sealed class DisplayProfileService
{
    /// <summary>Enumerate active display adapters.</summary>
    public IReadOnlyList<DisplayDevice> GetDisplays()
    {
        var results = new List<DisplayDevice>();
        try
        {
            var dd = new NativeMethods.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
            for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                bool active = (dd.StateFlags & NativeMethods.DisplayDeviceActive) != 0;
                if (active)
                {
                    bool primary = (dd.StateFlags & NativeMethods.DisplayDevicePrimary) != 0;
                    string adapter = dd.DeviceName;
                    string friendly = dd.DeviceString;

                    var mon = new NativeMethods.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
                    if (NativeMethods.EnumDisplayDevices(adapter, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString))
                        friendly = mon.DeviceString;

                    results.Add(new DisplayDevice(adapter, string.IsNullOrWhiteSpace(friendly) ? adapter : friendly, primary, active));
                }
                dd = new NativeMethods.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
            }
        }
        catch (DllNotFoundException ex) { Log.Warning("Display enumeration unavailable: {Error}", ex.Message); }
        return results;
    }

    /// <summary>The mode currently in effect for a device, or null if it can't be read.</summary>
    public DisplayMode? GetCurrentMode(string deviceName)
    {
        try
        {
            var dm = new NativeMethods.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>() };
            if (NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.EnumCurrentSettings, ref dm))
                return new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, (int)dm.dmBitsPerPel);
        }
        catch (DllNotFoundException ex) { Log.Warning("Read current display mode failed: {Error}", ex.Message); }
        return null;
    }

    /// <summary>All distinct supported modes (by width×height×refresh), newest-first by resolution then refresh.</summary>
    public IReadOnlyList<DisplayMode> GetSupportedModes(string deviceName)
    {
        var seen = new HashSet<(int, int, int)>();
        var modes = new List<DisplayMode>();
        try
        {
            var dm = new NativeMethods.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>() };
            for (int i = 0; NativeMethods.EnumDisplaySettings(deviceName, i, ref dm); i++)
            {
                int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight, hz = (int)dm.dmDisplayFrequency;
                if (hz > 1 && seen.Add((w, h, hz)))
                    modes.Add(new DisplayMode(w, h, hz, (int)dm.dmBitsPerPel));
                dm = new NativeMethods.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>() };
            }
        }
        catch (DllNotFoundException ex) { Log.Warning("Enumerate display modes failed: {Error}", ex.Message); }

        return modes
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.RefreshHz)
            .ToList();
    }

    /// <summary>
    /// Apply a mode for the current session (reverts on logoff/reboot). Validates with
    /// CDS_TEST first. Returns true on success; otherwise sets <paramref name="error"/>.
    /// </summary>
    public bool TryApplyMode(string deviceName, int width, int height, int refreshHz, out string error)
    {
        error = "";
        try
        {
            var dm = new NativeMethods.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>() };
            if (!NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.EnumCurrentSettings, ref dm))
            {
                error = "Could not read the current display mode.";
                return false;
            }

            dm.dmPelsWidth = (uint)width;
            dm.dmPelsHeight = (uint)height;
            dm.dmDisplayFrequency = (uint)refreshHz;
            dm.dmFields = NativeMethods.DM_PELSWIDTH | NativeMethods.DM_PELSHEIGHT | NativeMethods.DM_DISPLAYFREQUENCY;

            int test = NativeMethods.ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, NativeMethods.CDS_TEST, IntPtr.Zero);
            if (test != NativeMethods.DispChangeSuccessful)
            {
                error = Describe(test);
                return false;
            }

            int apply = NativeMethods.ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (apply == NativeMethods.DispChangeSuccessful) return true;

            error = Describe(apply);
            return false;
        }
        catch (DllNotFoundException ex) { error = $"Display API unavailable: {ex.Message}"; return false; }
        catch (EntryPointNotFoundException ex) { error = $"Display API missing: {ex.Message}"; return false; }
        catch (Win32Exception ex) { error = $"Display change failed: {ex.Message}"; return false; }
    }

    private static string Describe(int code) => code switch
    {
        NativeMethods.DispChangeRestart => "A restart is required for this mode.",
        NativeMethods.DispChangeFailed => "The display driver rejected the mode.",
        NativeMethods.DispChangeBadMode => "The requested mode is not supported by the display.",
        NativeMethods.DispChangeNotUpdated => "Could not save the display settings.",
        NativeMethods.DispChangeBadFlags => "Invalid flags passed to the display API.",
        NativeMethods.DispChangeBadParam => "Invalid parameter passed to the display API.",
        _ => $"The display change failed (code {code}).",
    };

    private static class NativeMethods
    {
        public const int EnumCurrentSettings = -1;
        public const uint DM_PELSWIDTH = 0x00080000;
        public const uint DM_PELSHEIGHT = 0x00100000;
        public const uint DM_DISPLAYFREQUENCY = 0x00400000;
        public const uint CDS_TEST = 0x00000002;
        public const uint DisplayDeviceActive = 0x00000001;
        public const uint DisplayDevicePrimary = 0x00000004;

        public const int DispChangeSuccessful = 0;
        public const int DispChangeRestart = 1;
        public const int DispChangeFailed = -1;
        public const int DispChangeBadMode = -2;
        public const int DispChangeNotUpdated = -3;
        public const int DispChangeBadFlags = -4;
        public const int DispChangeBadParam = -5;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public uint cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        // DEVMODEW. Union #1 modeled with the printer short-branch (8 shorts = 16 bytes),
        // which sizes the union correctly on x64; the display fields we use (width/height/
        // frequency) sit after it. dmSize is set from Marshal.SizeOf before every call.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;

            public short dmOrientation;
            public short dmPaperSize;
            public short dmPaperLength;
            public short dmPaperWidth;
            public short dmScale;
            public short dmCopies;
            public short dmDefaultSource;
            public short dmPrintQuality;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
    }
}
