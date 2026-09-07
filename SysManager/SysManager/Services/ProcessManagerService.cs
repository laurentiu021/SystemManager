// SysManager · ProcessManagerService — enumerate and manage running processes
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Enumerates running processes with CPU/memory usage. Kill is opt-in
/// and requires confirmation in the ViewModel layer.
/// </summary>
public sealed partial class ProcessManagerService
{
    private readonly Func<IReadOnlyDictionary<int, IntPtr>> _mainWindowsByPid;
    private readonly Func<IntPtr, bool> _isHungWindow;

    public ProcessManagerService() : this(QueryMainWindowsByPid, IsHungAppWindow) { }

    /// <summary>
    /// Test seam for the two window probes, so "one enumeration per refresh" can be asserted by counting
    /// rather than measured with a stopwatch.
    /// </summary>
    internal ProcessManagerService(Func<IReadOnlyDictionary<int, IntPtr>> mainWindowsByPid,
                                   Func<IntPtr, bool> isHungWindow)
    {
        _mainWindowsByPid = mainWindowsByPid;
        _isHungWindow = isHungWindow;
    }

    // knownPids: PIDs the caller already shows. Their identity (description/path) is static and the
    // view model keeps it on the surviving entry (ReconcileInto), so for those PIDs we skip the
    // expensive MainModule/FileVersionInfo/File.Exists reads and refresh only the volatile metrics.
    public Task<IReadOnlyList<ProcessEntry>> SnapshotAsync(IReadOnlySet<int>? knownPids = null, CancellationToken ct = default)
        => Task.Run(() => Snapshot(knownPids, ct), ct);

    private IReadOnlyList<ProcessEntry> Snapshot(IReadOnlySet<int>? knownPids, CancellationToken ct)
    {
        List<ProcessEntry> results = [];
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch (InvalidOperationException) { return results; }
        catch (System.ComponentModel.Win32Exception) { return results; }

        try
        {
            // First pass: capture CPU times
            var cpuStart = new Dictionary<int, (TimeSpan Cpu, DateTime Time)>();
            foreach (var p in procs)
            {
                if (ct.IsCancellationRequested) break;
                try { cpuStart[p.Id] = (p.TotalProcessorTime, DateTime.UtcNow); }
                catch (InvalidOperationException) { /* access denied — skip CPU for this process */ }
                catch (System.ComponentModel.Win32Exception) { /* access denied — skip CPU for this process */ }
            }

            // Brief pause to measure CPU delta (100ms is sufficient for meaningful readings)
            Thread.Sleep(100);

            int logicalCores = Environment.ProcessorCount;

            // One window enumeration for the whole refresh. Both properties this replaces resolve the
            // main window by walking every top-level window in the session, and they do NOT share the
            // result: measured on 479 processes, reading Responding cost 41.6 ms, reading
            // MainWindowHandle cost 43.6 ms, and reading both cost 81.4 ms — additive, so the walk ran
            // twice per process. Responding then also sent WM_NULL through SendMessageTimeout with
            // SMTO_ABORTIFHUNG and a 5000 ms timeout, which is most expensive in exactly the case the
            // column exists for: an app that has genuinely stopped pumping messages. One pass plus
            // IsHungAppWindow costs 0.4 ms for the same answers.
            var mainWindows = _mainWindowsByPid();

            foreach (var p in procs)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    // A process with no top-level window is not hung, it simply has no message loop to
                    // stall — which is also what Responding reported for it (it returns true when the
                    // main window handle is zero).
                    var hasWindow = mainWindows.TryGetValue(p.Id, out var mainWindow);

                    var entry = new ProcessEntry
                    {
                        Pid = p.Id,
                        Name = p.ProcessName,
                        MemoryBytes = p.WorkingSet64,
                        ThreadCount = p.Threads.Count,
                        HasMainWindow = hasWindow,
                        Status = hasWindow && _isHungWindow(mainWindow) ? "Not responding" : "Running"
                    };

                    // Calculate CPU %
                    if (cpuStart.TryGetValue(p.Id, out var start))
                    {
                        try
                        {
                            var cpuEnd = p.TotalProcessorTime;
                            var elapsed = (DateTime.UtcNow - start.Time).TotalMilliseconds;
                            if (elapsed > 0)
                                entry.CpuPercent = Math.Round((cpuEnd - start.Cpu).TotalMilliseconds / elapsed / logicalCores * 100, 1);
                        }
                        catch (InvalidOperationException) { /* process may have exited */ }
                        catch (System.ComponentModel.Win32Exception) { /* process may have exited */ }
                    }

                    // Identity (description/path) is static per process and the view model keeps it
                    // for a PID it already tracks, so only pay the expensive MainModule /
                    // FileVersionInfo / File.Exists cost for a PID we haven't seen yet.
                    if (knownPids is null || !knownPids.Contains(p.Id))
                    {
                        try
                        {
                            var module = p.MainModule;
                            entry.Description = module?.FileVersionInfo.FileDescription ?? "";
                            entry.FilePath = module?.FileName ?? "";
                        }
                        catch (InvalidOperationException) { /* access denied or process exited */ }
                        catch (System.ComponentModel.Win32Exception) { /* access denied or process exited */ }
                        entry.CanOpenFileLocation = !string.IsNullOrWhiteSpace(entry.FilePath)
                                                   && System.IO.File.Exists(entry.FilePath);
                    }
                    try { entry.StartTime = p.StartTime; }
                    catch (InvalidOperationException) { /* access denied or process exited */ }
                    catch (System.ComponentModel.Win32Exception) { /* access denied or process exited */ }

                    results.Add(entry);
                }
                catch (InvalidOperationException) { /* access denied for system processes — skip */ }
                catch (System.ComponentModel.Win32Exception) { /* access denied for system processes — skip */ }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }

        return results;
    }

    /// <summary>
    /// Maps each process id to its main window, in one pass over the session's top-level windows.
    /// </summary>
    /// <remarks>
    /// "Main window" is defined the same way <c>Process.MainWindowHandle</c> defines it — the first
    /// visible, unowned top-level window belonging to the process — so the responsiveness column and the
    /// "has a window" flag keep answering about the same window they did before. A dialog or tooltip is
    /// owned by its parent and is skipped, which is what stops a modal prompt from being mistaken for an
    /// app's main window.
    /// <para>Processes with no window are simply absent from the result rather than present with a zero
    /// handle: on this machine 15 of 479 processes had one, so a map of only the windows that exist is
    /// both smaller and the honest shape.</para>
    /// </remarks>
    private static IReadOnlyDictionary<int, IntPtr> QueryMainWindowsByPid()
    {
        var byPid = new Dictionary<int, IntPtr>();

        // EnumWindows walks in Z-order, so the first window accepted for a process is its topmost one —
        // the same one Process.MainWindowHandle settles on.
        var enumerated = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            if (GetWindow(window, GwOwner) != IntPtr.Zero) return true;   // owned: a dialog, not the main window
            if (GetWindowThreadProcessId(window, out var pid) != 0)
                byPid.TryAdd((int)pid, window);
            return true;
        }, IntPtr.Zero);

        // The callback never returns false, so false here is a real failure rather than an early stop. It
        // leaves an empty map, which reads as "nothing has a window" and reports every process as
        // responding — worth a line in the log rather than silence. Read immediately: the callback's own
        // GetWindowThreadProcessId also sets the last error, and EnumWindows returns after every callback.
        if (!enumerated)
            Log.Debug("ProcessManager: EnumWindows failed with {Error}; window state unavailable this refresh",
                Marshal.GetLastWin32Error());

        return byPid;
    }

    /// <summary>Callback for <see cref="EnumWindows"/>; returning false stops the enumeration.</summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    /// <summary>GW_OWNER — the owner of a window, or zero for an unowned top-level window.</summary>
    private const uint GwOwner = 4;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint command);

    /// <summary>
    /// True when a window has stopped pumping messages — the non-blocking answer to the question
    /// <c>Process.Responding</c> answered by sending it one and waiting up to five seconds.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsHungAppWindow(IntPtr window);

    /// <summary>
    /// Kill a process by PID. Returns true if successful.
    /// </summary>
    public static bool KillProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        // Kill(entireProcessTree: true) throws AggregateException when one or more descendants
        // could not be terminated. Report the failure through the bool contract like the rest,
        // rather than letting it escape to the (synchronous) Kill command and crash the app.
        catch (AggregateException) { return false; }
    }

    /// <summary>
    /// Open the file location of a process in Explorer.
    /// </summary>
    public static void OpenFileLocation(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;
        try
        {
            // Dispose the returned Process handle — we don't track the launched explorer.
            Process.Start(new ProcessStartInfo
            {
                FileName = SystemPaths.ResolveSystemTool("explorer.exe"),
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            })?.Dispose();
        }
        catch (InvalidOperationException ex) { Log.Warning(ex, "Failed to open file location: {Path}", filePath); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warning(ex, "Failed to open file location: {Path}", filePath); }
    }
}
