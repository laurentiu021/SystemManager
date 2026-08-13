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

/// <summary>
/// One installed memory module.
/// </summary>
/// <param name="Slot">
/// Which physical slot the module sits in, from WMI <c>DeviceLocator</c> ("DIMM0",
/// "ChannelA-DIMM1") with <c>BankLabel</c> as the fallback. DeviceLocator is what is silk-screened
/// on the board, so it is the name that lets someone actually find the module; BankLabel is often
/// just "BANK 0" or empty. The grid column has always been headed "Slot" — it previously showed
/// BankLabel.
/// </param>
/// <param name="SpeedMHz">The module's rated speed, from WMI <c>Speed</c>.</param>
/// <param name="ConfiguredSpeedMHz">
/// The speed the module is actually running at, from WMI <c>ConfiguredClockSpeed</c>. Lower than
/// <paramref name="SpeedMHz"/> means the RAM is not running at its rated speed — usually because
/// XMP/EXPO is off in the BIOS. 0 when Windows does not report it.
/// </param>
public sealed record MemoryModule(
    string Slot,
    string Manufacturer,
    double CapacityGB,
    uint SpeedMHz,
    uint ConfiguredSpeedMHz,
    string PartNumber)
{
    /// <summary>
    /// True when the module is running below its rated speed and both figures are known — the
    /// condition worth telling the user about.
    /// </summary>
    public bool IsUnderclocked =>
        SpeedMHz > 0 && ConfiguredSpeedMHz > 0 && ConfiguredSpeedMHz < SpeedMHz;
}

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
