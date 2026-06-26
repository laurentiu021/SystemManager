// SysManager · FileLockService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using SysManager.Models;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace SysManager.Services;

/// <summary>
/// Identifies which processes are holding a lock on (or otherwise using) a file or
/// folder, using the Windows Restart Manager (rstrtmgr.dll) — the same mechanism
/// Explorer uses for its "file in use" dialog. Enumeration works for a standard user;
/// terminating a locker owned by SYSTEM or another user requires elevation.
///
/// NOTE on interop style: this is the one place we use classic <c>[DllImport]</c> with
/// <c>CharSet.Unicode</c> rather than the project-preferred <c>[LibraryImport]</c>.
/// <c>RM_PROCESS_INFO</c> contains inline <c>ByValTStr</c> buffers (non-blittable), and
/// <c>RmStartSession</c> takes a <c>StringBuilder</c> out-buffer — neither is supported
/// by the <c>[LibraryImport]</c> source generator (it emits SYSLIB1051). The Restart
/// Manager functions have no A/W variants, so no <c>EntryPoint</c> suffix is needed.
/// </summary>
public sealed class FileLockService
{
    /// <summary>
    /// Returns the processes currently using <paramref name="path"/> (a file or folder).
    /// Empty when nothing holds it. Throws <see cref="ArgumentException"/> for bad input.
    /// </summary>
    public IReadOnlyList<FileLocker> FindLockers(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));

        var key = new StringBuilder(NativeMethods.CchRmSessionKey + 1); // 33 chars
        int rv = NativeMethods.RmStartSession(out uint handle, 0, key);
        if (rv != NativeMethods.ErrorSuccess)
        {
            Log.Debug("RmStartSession failed: {Code}", rv);
            return [];
        }

        try
        {
            string[] resources = [path];
            rv = NativeMethods.RmRegisterResources(handle,
                (uint)resources.Length, resources, 0, null, 0, null);
            if (rv != NativeMethods.ErrorSuccess)
            {
                Log.Debug("RmRegisterResources failed: {Code}", rv);
                return [];
            }

            const int maxRetries = 6;
            uint pnProcInfo = 0;
            NativeMethods.RM_PROCESS_INFO[]? info = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                rv = NativeMethods.RmGetList(handle, out uint needed, ref pnProcInfo, info, out _);

                if (rv == NativeMethods.ErrorSuccess)
                {
                    if (pnProcInfo == 0 || info is null) return [];
                    var result = new List<FileLocker>((int)pnProcInfo);
                    for (int i = 0; i < pnProcInfo; i++)
                        result.Add(Map(info[i]));
                    return result;
                }

                if (rv != NativeMethods.ErrorMoreData)
                {
                    Log.Debug("RmGetList failed: {Code}", rv);
                    return [];
                }

                // Restart Manager re-snapshots each call, so the count can grow — loop.
                pnProcInfo = needed;
                info = new NativeMethods.RM_PROCESS_INFO[needed];
            }

            Log.Debug("RmGetList: locker list kept growing past retry limit for {Path}", path);
            return [];
        }
        finally
        {
            NativeMethods.RmEndSession(handle);
        }
    }

    /// <summary>
    /// Terminates the process with the given id. Returns true on success. Returns false
    /// (and logs) if the process is gone, access is denied (needs elevation), or it exits
    /// on its own. Callers must confirm with the user first.
    /// </summary>
    public bool KillProcess(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            p.Kill();
            return true;
        }
        catch (ArgumentException)
        {
            // Process already exited / no such id.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception ex)
        {
            // Typically access denied — the target is higher integrity / another user.
            Log.Debug("Kill process {Pid} denied: {Error}", processId, ex.Message);
            return false;
        }
    }

    private static FileLocker Map(in NativeMethods.RM_PROCESS_INFO p)
    {
        DateTime? start = null;
        try
        {
            long ft = ((long)p.Process.ProcessStartTime.dwHighDateTime << 32)
                      | (uint)p.Process.ProcessStartTime.dwLowDateTime;
            if (ft > 0) start = DateTime.FromFileTime(ft);
        }
        catch (ArgumentOutOfRangeException) { /* leave start null on bad FILETIME */ }

        string name = string.IsNullOrWhiteSpace(p.strAppName) ? "(unknown)" : p.strAppName;
        return new FileLocker((int)p.Process.dwProcessId, name, p.ApplicationType.ToString(), start);
    }

    private static class NativeMethods
    {
        public const int ErrorSuccess = 0;
        public const int ErrorMoreData = 234;
        public const int CchRmSessionKey = 32;       // CCH_RM_SESSION_KEY (buffer = +1)
        private const int CchRmMaxAppName = 255;     // CCH_RM_MAX_APP_NAME
        private const int CchRmMaxSvcName = 63;      // CCH_RM_MAX_SVC_NAME

        [StructLayout(LayoutKind.Sequential)]
        public struct RM_UNIQUE_PROCESS
        {
            public uint dwProcessId;
            public FILETIME ProcessStartTime;
        }

        public enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000,
        }

        // CharSet.Unicode on the struct is REQUIRED so ByValTStr reads 2-byte WCHARs.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
            public string strAppName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
            public string strServiceShortName;

            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;

            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmRegisterResources(
            uint pSessionHandle,
            uint nFiles, string[]? rgsFilenames,
            uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications,
            uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmGetList(
            uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
            out uint lpdwRebootReasons);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmEndSession(uint pSessionHandle);
    }
}
