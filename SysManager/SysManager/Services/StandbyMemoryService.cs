// SysManager · StandbyMemoryService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads physical-memory stats (no admin) and purges the Windows standby memory list
/// (the same thing ISLC / RAMMap do). Purging is non-destructive: the standby list holds
/// clean, disk-backed file cache, so clearing it loses no data — Windows simply re-reads
/// from disk on next access. The purge requires an elevated token with
/// <c>SeProfileSingleProcessPrivilege</c> enabled; without it, it cleanly reports "needs
/// administrator" rather than silently failing.
/// </summary>
public sealed partial class StandbyMemoryService
{
    /// <summary>Read total / available / load%. Works without elevation.</summary>
    public MemoryStatus GetMemoryStatus()
    {
        var statex = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
        };
        try
        {
            if (!NativeMethods.GlobalMemoryStatusEx(ref statex))
            {
                Log.Debug("GlobalMemoryStatusEx failed: {Code}", Marshal.GetLastPInvokeError());
                return MemoryStatus.Empty;
            }
            return new MemoryStatus(statex.ullTotalPhys, statex.ullAvailPhys, statex.dwMemoryLoad);
        }
        catch (DllNotFoundException ex) { Log.Warning("Memory status unavailable: {Error}", ex.Message); return MemoryStatus.Empty; }
    }

    /// <summary>
    /// Enable the required privilege and purge the standby list. Returns true on success;
    /// otherwise sets <paramref name="error"/> (typically "needs administrator").
    /// </summary>
    public bool TryPurgeStandbyList(out string error)
    {
        error = "";
        try
        {
            EnableProfilePrivilege();
        }
        catch (UnauthorizedAccessException ex) { error = ex.Message; return false; }
        catch (Win32Exception ex) { error = $"Could not enable the required privilege ({ex.Message})."; return false; }
        catch (DllNotFoundException ex) { error = $"Memory API unavailable: {ex.Message}"; return false; }

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, NativeMethods.MemoryPurgeStandbyList);
            int status = NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, buffer, sizeof(int));
            if (status != NativeMethods.StatusSuccess)
            {
                error = status == NativeMethods.StatusPrivilegeNotHeld
                    ? "Purging the standby list requires administrator rights."
                    : $"The system rejected the purge (NTSTATUS 0x{status:X8}).";
                return false;
            }
            return true;
        }
        catch (DllNotFoundException ex) { error = $"Memory API unavailable: {ex.Message}"; return false; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void EnableProfilePrivilege()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(),
                NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_ADJUST_PRIVILEGES, out IntPtr hToken))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed.");

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out NativeMethods.LUID luid))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "LookupPrivilegeValue failed.");

            var privileges = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = NativeMethods.SE_PRIVILEGE_ENABLED,
            };

            // AdjustTokenPrivileges returns true even when the privilege isn't held —
            // the real signal is GetLastError == ERROR_NOT_ALL_ASSIGNED.
            if (!NativeMethods.AdjustTokenPrivileges(hToken, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "AdjustTokenPrivileges failed.");

            int lastError = Marshal.GetLastPInvokeError();
            if (lastError == NativeMethods.ERROR_NOT_ALL_ASSIGNED)
                throw new UnauthorizedAccessException("Purging the standby list requires administrator rights.");
            if (lastError != NativeMethods.ERROR_SUCCESS)
                throw new Win32Exception(lastError, "AdjustTokenPrivileges reported an error.");
        }
        finally
        {
            NativeMethods.CloseHandle(hToken);
        }
    }

    private static partial class NativeMethods
    {
        internal const int StatusSuccess = 0;
        internal const int StatusPrivilegeNotHeld = unchecked((int)0xC0000061);
        internal const int SystemMemoryListInformation = 80;
        internal const int MemoryPurgeStandbyList = 4;
        internal const uint TOKEN_QUERY = 0x0008;
        internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        internal const int ERROR_SUCCESS = 0;
        internal const int ERROR_NOT_ALL_ASSIGNED = 1300;

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [LibraryImport("ntdll.dll")]
        internal static partial int NtSetSystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [LibraryImport("kernel32.dll")]
        internal static partial IntPtr GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr hObject);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        // LookupPrivilegeValue has A/W variants → pin the W entry point explicitly.
        [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState,
            uint bufferLength,
            IntPtr previousState,
            IntPtr returnLength);
    }
}
