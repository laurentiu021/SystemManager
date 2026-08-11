// SysManager · TuneUpResult — results from the One-Click Tune-Up wizard
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// Aggregated results from a Quick Tune-Up run.
/// Each step populates its own section; null means the step was skipped.
/// </summary>
public sealed record TuneUpResult
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
    public string FreedDisplay => FormatHelper.FormatSize(TempBytesFreed);

    /// <summary>
    /// How many things the Tune-Up found worth mentioning. Drives <see cref="OverallVerdict"/> and
    /// <see cref="OverallColorHex"/>, so it is what the user reads as the headline.
    /// </summary>
    /// <remarks>
    /// A disk counts as a warning when its own <see cref="DiskHealthSummary.ColorHex"/> is not
    /// <see cref="StatusColors.Good"/>, not by comparing the verdict text.
    /// <para>This used to read <c>d.Verdict != "Healthy"</c>, and no code path ever produces that
    /// exact string: <c>DiskHealthService.ApplyVerdict</c> writes <c>"Healthy — 38 °C · wear 2% ·
    /// 4210 h on"</c> when any SMART counter is readable and <c>"Healthy."</c> — with a period —
    /// otherwise. So every healthy disk counted as a warning, and a perfectly fine PC with two
    /// drives reported "2 recommendations" in amber while listing two disks whose own text said
    /// "Healthy". The comparison was written against <c>MapHealth</c>, which does return a bare
    /// "Healthy", but that value feeds <c>HealthStatus</c> — a different property.</para>
    /// <para>Keyed on the colour because the service sets it on every one of its six verdict
    /// branches, exactly one of which is Good. A prefix test on the text would work today and break
    /// the moment someone rewords a message; the colour is the field whose whole purpose is to say
    /// "is this a problem".</para>
    /// </remarks>
    public int WarningCount
    {
        get
        {
            int count = 0;
            if (BrokenShortcutsFound > 0) count++;
            if (UptimeWarning) count++;
            if (RamWarning) count++;
            count += DiskResults.Count(d =>
                !string.Equals(d.ColorHex, StatusColors.Good, StringComparison.Ordinal));
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
        0 => StatusColors.Good,
        <= 2 => StatusColors.Warning,
        _ => StatusColors.Bad
    };
}

/// <summary>Per-disk summary for the Tune-Up result card.</summary>
public sealed record DiskHealthSummary
{
    public required string Name { get; init; }
    public required string Verdict { get; init; }
    public required string ColorHex { get; init; }
}
