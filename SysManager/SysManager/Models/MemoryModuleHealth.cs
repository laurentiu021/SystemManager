// SysManager · MemoryModuleHealth
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

public sealed class MemoryModuleHealth
{
    public string Slot { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public double CapacityGB { get; set; }
    public uint SpeedMHz { get; set; }
    public uint ConfiguredSpeedMHz { get; set; }
    public string PartNumber { get; set; } = "";
}
