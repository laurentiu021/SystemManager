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
    IReadOnlyList<NetworkAdapterInfo> NetworkAdapters);

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
