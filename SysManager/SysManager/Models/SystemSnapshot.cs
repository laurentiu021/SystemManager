// SysManager · SystemSnapshot
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

public sealed record MemoryInfo(
    double TotalGB,
    double AvailableGB,
    double UsedGB,
    double UsedPercent,
    IReadOnlyList<MemoryModule> Modules);

public sealed record MemoryModule(
    string BankLabel,
    string Manufacturer,
    double CapacityGB,
    uint SpeedMHz,
    string PartNumber);

public sealed record DiskInfo(
    string FriendlyName,
    string MediaType,
    string BusType,
    double SizeGB,
    string HealthStatus,
    string OperationalStatus,
    double? TemperatureC,
    int? WearPercent);

public sealed record CpuInfo(
    string Name,
    uint Cores,
    uint LogicalProcessors,
    uint MaxClockMHz,
    double LoadPercent);

public sealed record OsInfo(
    string Caption,
    string Version,
    string BuildNumber,
    TimeSpan Uptime,
    string Architecture);

public sealed record SystemSnapshot(
    OsInfo Os,
    CpuInfo Cpu,
    MemoryInfo Memory,
    IReadOnlyList<DiskInfo> Disks,
    DateTime CapturedAt);
