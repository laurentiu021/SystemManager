// SysManager · FileLockService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace SysManager.Services;

/// <summary>
/// Identifies which processes are holding a lock on (or otherwise using) a file or
/// folder, using the Windows Restart Manager (rstrtmgr.dll) — the same mechanism
/// Explorer uses for its "file in use" dialog. Enumeration works for a standard user;
/// terminating a locker owned by SYSTEM or another user requires elevation.
///
/// NOTE on interop style: this file uses classic <c>[DllImport]</c> with
/// <c>CharSet.Unicode</c> rather than the project-preferred <c>[LibraryImport]</c>.
/// <c>RM_PROCESS_INFO</c> contains inline <c>ByValTStr</c> buffers (non-blittable), and
/// <c>RmStartSession</c> takes a <c>StringBuilder</c> out-buffer — neither is supported
/// by the <c>[LibraryImport]</c> source generator (it emits SYSLIB1051). The Restart
/// Manager functions have no A/W variants, so no <c>EntryPoint</c> suffix is needed.
/// </summary>
public sealed class FileLockService : IFileLockService
{
    /// <summary>How many files inside a folder one check covers.</summary>
    /// <remarks>
    /// Measured, not guessed: a Restart Manager session with 1,000 registered files answered in about 200 ms and
    /// still found the one process holding one of them. A folder with more is checked for its first 1,000, and the
    /// result says so.
    /// </remarks>
    internal const int MaxFolderFiles = 1000;

    private readonly Func<int, DateTime, ProcessManagerService.KillOutcome> _killProcess;

    public FileLockService() : this(ProcessManagerService.KillProcess) { }

    /// <summary>Test seam: the same service with the call that ends a process supplied.</summary>
    /// <param name="killProcess">
    /// Ends a process by ID and listed start time. Injected so a test can see what <see cref="KillProcess"/> passes
    /// on without ending anything real.
    /// </param>
    internal FileLockService(Func<int, DateTime, ProcessManagerService.KillOutcome> killProcess)
    {
        _killProcess = killProcess ?? throw new ArgumentNullException(nameof(killProcess));
    }

    /// <summary>
    /// Returns the processes currently using <paramref name="path"/>. For a folder, that means the files inside it.
    /// Null when Restart Manager could not complete the check.
    /// </summary>
    /// <remarks>
    /// Restart Manager tracks files, not folders. Given a folder, <c>RmGetList</c> fails with
    /// <c>ERROR_ACCESS_DENIED</c>, elevated or not, and this used to read that failure as "no process" (#2502). A
    /// folder is now checked through its files, found with the same <see cref="SafeFileWalk"/> the cleanup services
    /// use, so a junction inside it is not followed out of the folder.
    /// <para>A failed check is null, not an empty list: "nothing is using it" is the one answer the user acts on, by
    /// trying to delete the thing again.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">No file or folder exists at <paramref name="path"/>.</exception>
    public FileLockScan? FindLockers(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));

        if (File.Exists(path))
            return QueryRestartManager([path]) is { } found ? new FileLockScan(found, IsFolder: false, 1, false) : null;

        // Restart Manager accepts a path that does not exist and reports nobody using it, which answered a typo with
        // "no process is using that path".
        if (!Directory.Exists(path))
            throw new FileNotFoundException("No file or folder exists at that path.", path);

        var files = SafeFileWalk.Files(path, CancellationToken.None, new SafeWalkOptions())
            .Take(MaxFolderFiles + 1)
            .ToList();
        var partial = files.Count > MaxFolderFiles;
        string[] resources = [.. files.Take(MaxFolderFiles)];
        if (resources.Length == 0) return new FileLockScan([], IsFolder: true, 0, false);

        return QueryRestartManager(resources) is { } lockers
            ? new FileLockScan(lockers, IsFolder: true, resources.Length, partial)
            : null;
    }

    /// <summary>The processes Restart Manager reports as using any of <paramref name="resources"/>, or null when it failed.</summary>
    internal static IReadOnlyList<FileLocker>? QueryRestartManager(string[] resources)
    {
        var key = new StringBuilder(NativeMethods.CchRmSessionKey + 1); // 33 chars
        int rv = NativeMethods.RmStartSession(out uint handle, 0, key);
        if (rv != NativeMethods.ErrorSuccess)
        {
            Log.Debug("RmStartSession failed: {Code}", rv);
            return null;
        }

        try
        {
            rv = NativeMethods.RmRegisterResources(handle,
                (uint)resources.Length, resources, 0, null, 0, null);
            if (rv != NativeMethods.ErrorSuccess)
            {
                Log.Debug("RmRegisterResources failed: {Code}", rv);
                return null;
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
                    return null;
                }

                // Restart Manager re-snapshots each call, so the count can grow — loop.
                pnProcInfo = needed;
                info = new NativeMethods.RM_PROCESS_INFO[needed];
            }

            Log.Debug("RmGetList: locker list kept growing past retry limit for {Count} resource(s)", resources.Length);
            return null;
        }
        finally
        {
            NativeMethods.RmEndSession(handle);
        }
    }

    /// <summary>
    /// Ends the process with <paramref name="processId"/>, and only if it is the locker that was listed: a process
    /// with that ID that started at another time is left alone and reported as
    /// <see cref="ProcessManagerService.KillOutcome.NotRunning"/>. Callers must confirm with the user first.
    /// </summary>
    /// <param name="processId">The locker's process ID.</param>
    /// <param name="startTime">
    /// When the locker started, as Restart Manager reported it (<see cref="FileLocker.StartTime"/>), or null when it
    /// did not say, which skips the check. The list can be minutes old, and Windows gives a closed process's ID to
    /// the next one started, so the ID alone can name a different program (#2514). Restart Manager's time is the
    /// creation time <c>GetProcessTimes</c> reports, the one <see cref="Process.StartTime"/> reads.
    /// </param>
    /// <remarks>
    /// Process Manager's <see cref="ProcessManagerService.KillProcess"/> does the work, so the two tabs end a process
    /// the same way: the process alone, not its tree, after the same start-time check.
    /// </remarks>
    public ProcessManagerService.KillOutcome KillProcess(int processId, DateTime? startTime)
    {
        var outcome = _killProcess(processId, startTime ?? default);
        if (outcome == ProcessManagerService.KillOutcome.Refused)
            Log.Debug("Kill process {Pid} refused: it may need administrator rights", processId);
        return outcome;
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

    /// <summary>Renders the locking-process list as CSV with a header row.</summary>
    /// <remarks>
    /// <c>IsCritical</c> is exported as its own column rather than left inside <c>AppType</c>: a process the
    /// Restart Manager flags as critical is the one the user must NOT be told to end, and that warning has to
    /// survive into a file someone else may act on.
    /// </remarks>
    public static string ToCsv(IEnumerable<FileLocker> lockers)
    {
        ArgumentNullException.ThrowIfNull(lockers);

        var sb = new StringBuilder();
        Csv.AppendRow(sb, "PID", "Process", "Type", "Started", "Started (raw)", "Critical");
        foreach (var l in lockers)
        {
            Csv.AppendRow(sb,
                l.ProcessId.ToString(CultureInfo.InvariantCulture),
                l.ProcessName,
                l.AppType,
                l.StartTimeDisplay,
                l.StartTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                l.IsCritical ? "yes" : "no");
        }
        return sb.ToString();
    }
}
