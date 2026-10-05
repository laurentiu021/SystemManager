// SysManager · SystemReportData — structured payload for the System Report (text/HTML/JSON)
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A complete, format-agnostic system report. Gathered once, then rendered to
/// plain text, HTML, or JSON so all three outputs share a single source of truth.
/// </summary>
public sealed record SystemReportData(
    DateTime GeneratedAt,
    string AppVersion,
    OsInfo Os,
    CpuInfo Cpu,
    MemoryInfo Memory,
    IReadOnlyList<GpuReportInfo> Gpus,
    string Motherboard,
    IReadOnlyList<DiskReportInfo> Disks,
    IReadOnlyList<NetworkAdapterInfo> NetworkAdapters)
{
    /// <summary>
    /// The verdict the report opens with: the health score, what to do about it, and the recent problems
    /// Windows logged (#1508). Null when the report was built without one.
    /// </summary>
    /// <remarks>
    /// An init property rather than a tenth positional parameter, so every existing construction keeps
    /// compiling and a payload without it still renders: each renderer skips the section when it is absent.
    /// </remarks>
    public ReportHealth? Health { get; init; }
}

/// <summary>
/// The health part of the report: the same score and recommendations the Dashboard shows, with each
/// component's score, and the recent critical and error events from the System log.
/// </summary>
public sealed record ReportHealth(
    int Score,
    string Label,
    IReadOnlyList<ReportHealthComponent> Components,
    IReadOnlyList<ReportRecommendation> Recommendations,
    IReadOnlyList<ReportProblem> RecentProblems,
    bool ProblemsRead);

/// <summary>One part of the score. <see cref="Score"/> is null when Windows gave nothing to score it from.</summary>
public sealed record ReportHealthComponent(string Name, int? Score);

/// <summary>A recommendation as the Dashboard words it, with its severity: "warning" or "critical".</summary>
public sealed record ReportRecommendation(string Message, string Severity);

/// <summary>
/// One kind of recent critical or error event: its source, its ID, how many times it was logged in the window
/// and when last, and, when someone wrote it down, what it means and what to do. Never the event's own message,
/// which can name paths and machines.
/// </summary>
public sealed record ReportProblem(
    DateTime LastSeen,
    string Source,
    int EventId,
    bool Critical,
    int Count,
    string? Explanation,
    string? Recommendation);

/// <summary>A GPU as reported by Win32_VideoController.</summary>
public sealed record GpuReportInfo(
    string Name,
    double? VramGB,
    string DriverVersion);

/// <summary>
/// A disk in the report. Carries the richer SMART/health fields when available
/// (from <see cref="DiskHealthReport"/>) and falls back to basic snapshot fields.
/// </summary>
public sealed record DiskReportInfo(
    string FriendlyName,
    string MediaType,
    string BusType,
    double SizeGB,
    string HealthStatus,
    string? Verdict,
    double? TemperatureC,
    int? WearPercent,
    string? PowerOnDisplay);

/// <summary>An active network adapter from Win32_NetworkAdapterConfiguration.</summary>
public sealed record NetworkAdapterInfo(
    string Description,
    string IPv4,
    string MacAddress,
    bool DhcpEnabled);
