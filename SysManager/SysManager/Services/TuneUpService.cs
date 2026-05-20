// SysManager · TuneUpService — orchestrates the One-Click Tune-Up wizard
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Runs a sequence of safe, non-destructive checks and lightweight cleanup:
/// 1. Clear user/system TEMP files
/// 2. Empty Recycle Bin (only if caller confirms)
/// 3. Scan for broken shortcuts (report only)
/// 4. Check disk SMART health
/// 5. Check system uptime (warn if 14+ days)
/// 6. Check RAM usage
///
/// No admin required. No registry edits. No service changes.
/// </summary>
public sealed class TuneUpService
{
    private readonly ShortcutCleanerService _shortcuts;
    private readonly DiskHealthService _diskHealth;
    private readonly SystemInfoService _sysInfo;

    public TuneUpService(
        ShortcutCleanerService shortcuts,
        DiskHealthService diskHealth,
        SystemInfoService sysInfo)
    {
        _shortcuts = shortcuts;
        _diskHealth = diskHealth;
        _sysInfo = sysInfo;
    }

    /// <summary>
    /// Runs the full tune-up sequence, reporting progress for each step.
    /// </summary>
    /// <param name="emptyRecycleBin">True if the user confirmed Recycle Bin emptying.</param>
    /// <param name="progress">Reports (stepIndex 0-5, stepName).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TuneUpResult> RunAsync(
        bool emptyRecycleBin,
        IProgress<(int Step, string Message)>? progress = null,
        CancellationToken ct = default)
    {
        // Step 1: Temp cleanup
        progress?.Report((0, "Cleaning temporary files…"));
        var (tempFreed, tempDeleted, tempErrors) = await CleanTempFilesAsync(ct);
        ct.ThrowIfCancellationRequested();

        // Step 2: Recycle Bin
        progress?.Report((1, "Emptying Recycle Bin…"));
        bool binEmptied = false;
        bool binSkipped = !emptyRecycleBin;
        if (emptyRecycleBin)
        {
            binEmptied = await EmptyRecycleBinAsync(ct);
        }
        ct.ThrowIfCancellationRequested();

        // Step 3: Broken shortcuts scan
        progress?.Report((2, "Scanning shortcuts…"));
        int brokenCount = 0;
        try
        {
            var broken = await _shortcuts.ScanAsync(ct: ct);
            brokenCount = broken.Count;
        }
        catch (IOException ex)
        {
            Log.Warning("TuneUp shortcut scan failed: {Error}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning("TuneUp shortcut scan failed: {Error}", ex.Message);
        }
        ct.ThrowIfCancellationRequested();

        // Step 4: Disk SMART
        progress?.Report((3, "Checking disk health…"));
        List<DiskHealthSummary> diskSummaries = [];
        try
        {
            var reports = await _diskHealth.CollectAsync(ct);
            foreach (var r in reports)
            {
                diskSummaries.Add(new DiskHealthSummary
                {
                    Name = r.FriendlyName,
                    Verdict = r.HealthStatus,
                    ColorHex = r.VerdictColorHex
                });
            }
        }
        catch (System.Management.ManagementException ex)
        {
            Log.Warning("TuneUp disk health check failed: {Error}", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("TuneUp disk health check failed: {Error}", ex.Message);
        }
        ct.ThrowIfCancellationRequested();

        // Step 5: Uptime + RAM
        progress?.Report((4, "Checking system vitals…"));
        TimeSpan uptime = TimeSpan.Zero;
        double ramUsedPct = 0, ramUsedGB = 0, ramTotalGB = 0;
        try
        {
            var snapshot = await _sysInfo.CaptureAsync(ct);
            uptime = snapshot.Os.Uptime;
            ramUsedPct = snapshot.Memory.UsedPercent;
            ramUsedGB = snapshot.Memory.UsedGB;
            ramTotalGB = snapshot.Memory.TotalGB;
        }
        catch (System.Management.ManagementException ex)
        {
            Log.Warning("TuneUp system info failed: {Error}", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("TuneUp system info failed: {Error}", ex.Message);
        }

        progress?.Report((5, "Done"));

        return new TuneUpResult
        {
            TempBytesFreed = tempFreed,
            TempFilesDeleted = tempDeleted,
            TempErrors = tempErrors,
            RecycleBinEmptied = binEmptied,
            RecycleBinSkipped = binSkipped,
            BrokenShortcutsFound = brokenCount,
            DiskResults = diskSummaries,
            Uptime = uptime,
            RamUsedPercent = ramUsedPct,
            RamUsedGB = ramUsedGB,
            RamTotalGB = ramTotalGB
        };
    }

    // ── Temp file cleanup ──────────────────────────────────────────────

    private static Task<(long BytesFreed, int FilesDeleted, int Errors)> CleanTempFilesAsync(CancellationToken ct)
        => Task.Run(() => CleanTempFiles(ct), ct);

    private static (long BytesFreed, int FilesDeleted, int Errors) CleanTempFiles(CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0;
        int errors = 0;

        var tempPaths = new[]
        {
            Environment.GetEnvironmentVariable("TEMP") ?? "",
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
        };

        foreach (var dir in tempPaths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        long size = info.Length;
                        info.Delete();
                        freed += size;
                        deleted++;
                    }
                    catch (IOException) { errors++; }
                    catch (UnauthorizedAccessException) { errors++; }
                }

                // Try to remove empty subdirectories
                // FUNC-M3: Sort by directory depth (separator count) descending,
                // not string length. A path like "C:\a\b\c" is deeper than
                // "C:\longname" despite being shorter — we must delete deepest first.
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(sub).Any())
                            Directory.Delete(sub);
                    }
                    catch (IOException) { /* Directory in use or locked — skip */ }
                    catch (UnauthorizedAccessException) { /* No permission to delete — skip */ }
                }
            }
            catch (IOException ex)
            {
                Log.Warning("TuneUp temp cleanup error in {Dir}: {Error}", dir, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning("TuneUp temp cleanup error in {Dir}: {Error}", dir, ex.Message);
            }
        }

        return (freed, deleted, errors);
    }

    // ── Recycle Bin ────────────────────────────────────────────────────

    private static Task<bool> EmptyRecycleBinAsync(CancellationToken ct)
        => Task.Run(() => EmptyRecycleBin(), ct);

    private static bool EmptyRecycleBin()
    {
        try
        {
            // SHEmptyRecycleBin with SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
            int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, 0x00000007);
            // S_OK (0) = success, 0x80070012 (ERROR_NO_MORE_FILES) = bin already empty
            return hr >= 0 || unchecked((uint)hr) == 0x80070012;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("TuneUp empty recycle bin failed: {Error}", ex.Message);
            return false;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    }
}
