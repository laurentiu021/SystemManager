// SysManager · TuneUpService — orchestrates the One-Click Tune-Up wizard
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Serilog;
using SysManager.Helpers;
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
public sealed class TuneUpService : ITuneUpService
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
        var (tempFreed, tempDeleted, tempErrors) = await CleanTempFilesAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Step 2: Recycle Bin
        progress?.Report((1, "Emptying Recycle Bin…"));
        bool binEmptied = false;
        bool binSkipped = !emptyRecycleBin;
        if (emptyRecycleBin)
        {
            binEmptied = await EmptyRecycleBinAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();

        // Step 3: Broken shortcuts scan
        progress?.Report((2, "Scanning shortcuts…"));
        var brokenCount = await CountBrokenShortcutsAsync(() => _shortcuts.ScanAsync(ct: ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Step 4: Disk SMART
        progress?.Report((3, "Checking disk health…"));
        var diskSummaries = await ReadDisksAsync(() => _diskHealth.CollectAsync(ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Step 5: Uptime + RAM
        progress?.Report((4, "Checking system vitals…"));
        var snapshot = await CaptureVitalsAsync(() => _sysInfo.CaptureAsync(ct)).ConfigureAwait(false);

        progress?.Report((5, "Done"));

        return new TuneUpResult
        {
            TempBytesFreed = tempFreed,
            TempFilesDeleted = tempDeleted,
            TempErrors = tempErrors,
            RecycleBinEmptied = binEmptied,
            RecycleBinSkipped = binSkipped,
            BrokenShortcutsFound = brokenCount ?? 0,
            DiskResults = diskSummaries ?? [],
            Uptime = snapshot?.Os.Uptime ?? TimeSpan.Zero,
            RamUsedPercent = snapshot?.Memory.UsedPercent ?? 0,
            RamUsedGB = snapshot?.Memory.UsedGB ?? 0,
            RamTotalGB = snapshot?.Memory.TotalGB ?? 0,
            NotChecked = NotChecked(brokenCount, diskSummaries, snapshot),
        };
    }

    // ── The three checks ───────────────────────────────────────────────
    //
    // Each returns null when it could not run, which the result lists in NotChecked. They used to leave their
    // field at the value that means "nothing wrong", so a failed check read as a passed one (#2501). Internal and
    // handed the call to make, so the failure paths are testable without the temp clean and the Recycle Bin that
    // RunAsync does first.

    /// <summary>Step 3: how many shortcuts point at files that no longer exist, or null when the scan failed.</summary>
    internal static async Task<int?> CountBrokenShortcutsAsync(Func<Task<ShortcutScanReport>> scan)
    {
        try
        {
            // .Broken.Count, not every shortcut the scan looked at: the report also carries the ones whose
            // target it could not reach, and those are explicitly NOT broken (#2378). Counting them here
            // would put "3 broken shortcuts" on the Tune-Up card for a machine with an unplugged drive.
            var report = await scan().ConfigureAwait(false);
            return report.Broken.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("TuneUp shortcut scan failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Step 4: one row per disk, or null when no disk could be read.
    /// </summary>
    /// <remarks>
    /// An empty list counts as not checked. <c>DiskHealthService</c> swallows its own WMI failures and returns what
    /// it has, which is nothing when the Storage namespace cannot be reached, and a PC with no disk at all is not a
    /// case this card has to describe.
    /// </remarks>
    internal static async Task<List<DiskHealthSummary>?> ReadDisksAsync(Func<Task<IReadOnlyList<DiskHealthReport>>> collect)
    {
        try
        {
            var reports = await collect().ConfigureAwait(false);
            if (reports.Count == 0) return null;

            return [.. reports.Select(r => new DiskHealthSummary
            {
                Name = r.FriendlyName,
                // Verdict, NOT HealthStatus. HealthStatus is the raw WMI enum word that MapHealth
                // produces ("Healthy" / "Warning" / "Unhealthy"); Verdict is the plain-English
                // sentence ApplyVerdict writes, which is what every other surface shows and what
                // ColorHex on the next line is derived from. Taking the enum here put an amber
                // "1 recommendation" headline next to a disk row reading plainly "Healthy" — the
                // card contradicting itself, on the one tab a non-technical user opens to find out
                // whether their PC is fine (#1785).
                Verdict = r.Verdict,
                ColorHex = r.VerdictColorHex
            })];
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            Log.Warning("TuneUp disk health check failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>Step 5: uptime and memory, or null when they could not be read.</summary>
    /// <remarks>
    /// <c>COMException</c> as well as the two it caught before: WMI enumeration raises it on repository and RPC
    /// failures, as <c>HealthScoreService</c> already allows for, and uncaught it failed the whole Tune-Up after the
    /// clean-up had run.
    /// </remarks>
    internal static async Task<SystemSnapshot?> CaptureVitalsAsync(Func<Task<SystemSnapshot>> capture)
    {
        try
        {
            return await capture().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            Log.Warning("TuneUp system info failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>What the result lists as not checked, in the order the card reads.</summary>
    internal static List<string> NotChecked(int? brokenShortcuts, List<DiskHealthSummary>? disks, SystemSnapshot? snapshot)
    {
        List<string> notChecked = [];
        if (brokenShortcuts is null) notChecked.Add("shortcuts");
        if (disks is null) notChecked.Add("the disks");
        if (snapshot is null)
        {
            notChecked.Add("memory");
            notChecked.Add("uptime");
        }
        return notChecked;
    }

    // ── Temp file cleanup ──────────────────────────────────────────────

    /// <summary>
    /// Cleans user + Windows TEMP, never descending into reparse points (junctions /
    /// symbolic links) so a link inside TEMP can't redirect deletion to unrelated data.
    /// Returns the bytes freed, files deleted, and per-file error count. Shared by the
    /// full Tune-Up sequence and the Dashboard's Quick Cleanup shortcut so both use this
    /// one safe implementation.
    /// </summary>
    public static Task<(long BytesFreed, int FilesDeleted, int Errors)> CleanTempFilesAsync(CancellationToken ct = default)
        => Task.Run(() => CleanTempFiles(ct), ct);

    /// <summary>The same sweep, reached through the seam, so that Quick Cleanup can be tested without running it.</summary>
    Task<(long BytesFreed, int FilesDeleted, int Errors)> ITuneUpService.CleanTempFilesAsync(CancellationToken ct)
        => CleanTempFilesAsync(ct);

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
                foreach (var file in SafeFileWalk.Files(dir, ct, TempWalk))
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

                // Remove the emptied subdirectories, deepest first — the order the walk already returns,
                // so there is no re-sort here to get wrong.
                foreach (var sub in SafeFileWalk.DirectoriesDeepestFirst(dir, ct, TempWalk))
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

    /// <summary>
    /// How the temp sweep walks: reparse-point-safe, and never into the folder single-file .NET apps
    /// unpack themselves into. Shared with <see cref="CleanupPreScanService"/> so the size it shows the
    /// user is measured over exactly the files the clean would delete.
    /// </summary>
    internal static SafeWalkOptions TempWalk { get; } = new()
    {
        ExcludeSubtrees = [SystemPaths.BundleExtractionRoot, SystemPaths.OwnExtractionDirectory],
    };

    // ── Recycle Bin ────────────────────────────────────────────────────

    private static Task<bool> EmptyRecycleBinAsync(CancellationToken ct)
        => Task.Run(RecycleBinHelper.EmptyAllDrives, ct);
}
