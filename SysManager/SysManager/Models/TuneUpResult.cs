// SysManager · TuneUpResult — results from the One-Click Tune-Up wizard
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Aggregated results from a Quick Tune-Up run.
/// Each step populates its own section; null means the step was skipped.
/// </summary>
public sealed class TuneUpResult
{
    // ── Temp cleanup ───────────────────────────────────────────────────
    public long TempBytesFreed { get; init; }
    public int TempFilesDeleted { get; init; }
    public int TempErrors { get; init; }

    // ── Recycle Bin ────────────────────────────────────────────────────
    public bool RecycleBinEmptied { get; init; }
    public bool RecycleBinSkipped { get; init; }

    // ── Broken shortcuts ───────────────────────────────────────────────
    public int BrokenShortcutsFound { get; init; }

    // ── Disk health ────────────────────────────────────────────────────
    public IReadOnlyList<DiskHealthSummary> DiskResults { get; init; } = [];

    // ── Uptime ─────────────────────────────────────────────────────────
    public TimeSpan Uptime { get; init; }
    public bool UptimeWarning => Uptime.TotalDays >= 14;

    // ── RAM ────────────────────────────────────────────────────────────
    public double RamUsedPercent { get; init; }
    public double RamUsedGB { get; init; }
    public double RamTotalGB { get; init; }
    public bool RamWarning => RamUsedPercent >= 85;

    // ── Summary helpers ────────────────────────────────────────────────
    public string FreedDisplay => CleanupCategory.HumanSize(TempBytesFreed);

    public int WarningCount
    {
        get
        {
            int count = 0;
            if (BrokenShortcutsFound > 0) count++;
            if (UptimeWarning) count++;
            if (RamWarning) count++;
            count += DiskResults.Count(d => d.Verdict != "Healthy");
            return count;
        }
    }

    public string OverallVerdict => WarningCount switch
    {
        0 => "All good",
        1 => "1 recommendation",
        _ => $"{WarningCount} recommendations"
    };

    public string OverallColorHex => WarningCount switch
    {
        0 => "#22C55E",
        <= 2 => "#F59E0B",
        _ => "#EF4444"
    };
}

/// <summary>Per-disk summary for the Tune-Up result card.</summary>
public sealed class DiskHealthSummary
{
    public required string Name { get; init; }
    public required string Verdict { get; init; }
    public required string ColorHex { get; init; }
}
