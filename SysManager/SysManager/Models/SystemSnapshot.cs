// SysManager · SystemSnapshot
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.Models;

public record MemoryInfo(
    double TotalGB,
    double AvailableGB,
    double UsedGB,
    double UsedPercent,
    List<MemoryModule> Modules);

public record MemoryModule(
    string BankLabel,
    string Manufacturer,
    double CapacityGB,
    uint SpeedMHz,
    string PartNumber);

public record DiskInfo(
    string FriendlyName,
    string MediaType,
    string BusType,
    double SizeGB,
    string HealthStatus,
    string OperationalStatus,
    double? TemperatureC,
    int? WearPercent);

public record CpuInfo(
    string Name,
    uint Cores,
    uint LogicalProcessors,
    uint MaxClockMHz,
    double LoadPercent);

public record OsInfo(
    string Caption,
    string Version,
    string BuildNumber,
    TimeSpan Uptime,
    string Architecture);

public record SystemSnapshot(
    OsInfo Os,
    CpuInfo Cpu,
    MemoryInfo Memory,
    List<DiskInfo> Disks,
    DateTime CapturedAt);
