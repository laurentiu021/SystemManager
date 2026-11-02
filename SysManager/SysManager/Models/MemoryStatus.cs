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
/// <remarks>
/// A purge moves the standby list's pages to the free list, so it leaves available memory where it was: what it changes
/// is <see cref="FreeBytes"/> and <see cref="StandbyBytes"/>, which auto-purge and the command line's payload read for
/// that reason (#2768). Both are null when Windows would not report its memory lists.
/// </remarks>
/// <param name="TotalBytes">Physical memory.</param>
/// <param name="AvailableBytes">The free, zero and standby lists together.</param>
/// <param name="LoadPercent">Windows' memory load.</param>
/// <param name="FreeBytes">The free and zero lists: memory no one holds, cache included.</param>
/// <param name="StandbyBytes">The standby list, at every priority: the file cache a purge drops.</param>
public readonly record struct MemoryStatus(
    ulong TotalBytes, ulong AvailableBytes, uint LoadPercent, ulong? FreeBytes = null, ulong? StandbyBytes = null)
{
    public string TotalDisplay => FormatHelper.FormatSize((long)TotalBytes);
    public string AvailableDisplay => FormatHelper.FormatSize((long)AvailableBytes);
    public string LoadDisplay => $"{LoadPercent}%";

    /// <summary>Available memory in MB.</summary>
    public double AvailableMb => AvailableBytes / (1024.0 * 1024.0);

    /// <summary>The free and zero lists in MB, or null when Windows would not report them.</summary>
    public double? FreeMb => FreeBytes / (1024.0 * 1024.0);

    /// <summary>The standby list in MB, or null when Windows would not report it.</summary>
    public double? StandbyMb => StandbyBytes / (1024.0 * 1024.0);

    public static MemoryStatus Empty { get; } = new(0, 0, 0);
}
