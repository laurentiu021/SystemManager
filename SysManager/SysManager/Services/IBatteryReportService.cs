// SysManager · IBatteryReportService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>How a read of the Windows battery report went.</summary>
public enum BatteryReportOutcome
{
    /// <summary>The report was read and holds capacity history for the battery fitted now.</summary>
    Read,

    /// <summary>The report was read and holds no capacity history: a desktop, or a battery Windows does not track.</summary>
    NoHistory,

    /// <summary>The report could not be produced or read, which is not the same as "no history".</summary>
    Failed,
}

/// <summary>The capacity history read from the battery report, oldest first, and how the read went.</summary>
public sealed record BatteryReportRead(BatteryReportOutcome Outcome, IReadOnlyList<BatteryCapacityPoint> History)
{
    /// <summary>A read that produced nothing usable.</summary>
    public static BatteryReportRead Failed { get; } = new(BatteryReportOutcome.Failed, []);
}

/// <summary>
/// Reads the capacity history Windows keeps for the battery, from <c>powercfg /batteryreport</c> (#1513).
/// </summary>
public interface IBatteryReportService
{
    /// <summary>Produces the battery report, reads its history and removes the file again.</summary>
    Task<BatteryReportRead> ReadHistoryAsync(CancellationToken ct = default);
}
