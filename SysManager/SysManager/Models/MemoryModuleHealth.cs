// SysManager · MemoryModuleHealth
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

public sealed class MemoryModuleHealth
{
    public string Slot { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public double CapacityGB { get; init; }
    public uint SpeedMHz { get; init; }
    public uint ConfiguredSpeedMHz { get; init; }
    public string PartNumber { get; init; } = "";
}
