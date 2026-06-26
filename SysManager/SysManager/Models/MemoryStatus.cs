// SysManager · MemoryStatus
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// A physical-memory snapshot for the Standby List Cleaner. <see cref="AvailableBytes"/>
/// is the sum of the free, zero, and standby lists (what Windows reports as "available"),
/// not pure free memory.
/// </summary>
public readonly record struct MemoryStatus(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent)
{
    public string TotalDisplay => FormatHelper.FormatSize((long)TotalBytes);
    public string AvailableDisplay => FormatHelper.FormatSize((long)AvailableBytes);
    public string LoadDisplay => $"{LoadPercent}%";

    /// <summary>Available memory in MB (used for threshold comparisons).</summary>
    public double AvailableMb => AvailableBytes / (1024.0 * 1024.0);

    public static MemoryStatus Empty { get; } = new(0, 0, 0);
}
