// SysManager · ProcessManagerService — enumerate and manage running processes
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Diagnostics;
using System.IO;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Enumerates running processes with CPU/memory usage. Kill is opt-in
/// and requires confirmation in the ViewModel layer.
/// </summary>
public sealed class ProcessManagerService
{
    public Task<IReadOnlyList<ProcessEntry>> SnapshotAsync(CancellationToken ct = default)
        => Task.Run(() => Snapshot(ct), ct);

    private static IReadOnlyList<ProcessEntry> Snapshot(CancellationToken ct)
    {
        var results = new List<ProcessEntry>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch (InvalidOperationException) { return results; }
        catch (System.ComponentModel.Win32Exception) { return results; }

        // First pass: capture CPU times
        var cpuStart = new Dictionary<int, (TimeSpan Cpu, DateTime Time)>();
        foreach (var p in procs)
        {
            if (ct.IsCancellationRequested) break;
            try { cpuStart[p.Id] = (p.TotalProcessorTime, DateTime.UtcNow); }
            catch (InvalidOperationException) { /* access denied — skip CPU for this process */ }
            catch (System.ComponentModel.Win32Exception) { /* access denied — skip CPU for this process */ }
        }

        // Brief pause to measure CPU delta
        Thread.Sleep(250);

        int logicalCores = Environment.ProcessorCount;

        foreach (var p in procs)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var entry = new ProcessEntry
                {
                    Pid = p.Id,
                    Name = p.ProcessName,
                    MemoryBytes = p.WorkingSet64,
                    ThreadCount = p.Threads.Count,
                    Status = p.Responding ? "Running" : "Not responding"
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

                try { entry.Description = p.MainModule?.FileVersionInfo.FileDescription ?? ""; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { entry.FilePath = p.MainModule?.FileName ?? ""; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { entry.StartTime = p.StartTime; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { entry.HasMainWindow = p.MainWindowHandle != IntPtr.Zero; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }

                results.Add(entry);
            }
            catch (InvalidOperationException) { /* access denied for system processes — skip */ }
            catch (System.ComponentModel.Win32Exception) { /* access denied for system processes — skip */ }
            finally { p.Dispose(); }
        }

        return results;
    }

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
    }

    /// <summary>
    /// Open the file location of a process in Explorer.
    /// </summary>
    public static void OpenFileLocation(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            });
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
