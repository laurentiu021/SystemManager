// SysManager · RestorePoint
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A Windows System Restore point as reported by <c>Get-ComputerRestorePoint</c>.
/// Immutable snapshot: the sequence number and creation time identify it uniquely.
/// </summary>
public sealed record RestorePoint(
    int SequenceNumber,
    string Description,
    DateTime CreationTime,
    string RestorePointType,
    string EventType)
{
    /// <summary>Human-readable creation timestamp for the grid.</summary>
    public string CreatedDisplay => CreationTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// Friendly restore-point type label. Windows reports a small set of well-known
    /// type strings; anything unrecognized falls through to the raw value.
    /// </summary>
    public string TypeDisplay => RestorePointType switch
    {
        "APPLICATION_INSTALL" => "App install",
        "APPLICATION_UNINSTALL" => "App uninstall",
        "DEVICE_DRIVER_INSTALL" => "Driver install",
        "MODIFY_SETTINGS" => "Manual / settings",
        "CANCELLED_OPERATION" => "Cancelled operation",
        _ => RestorePointType
    };
}
