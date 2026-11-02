// SysManager · RestorePoint
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

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
    public string CreatedDisplay => CreationTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Friendly restore-point type label. Windows reports the type as a number, kept here as text: the names a
    /// console shows come from PowerShell's table view, which the list's <c>Select-Object</c> bypasses, so the column
    /// read "12" and "0" (#2754). The names are read too. A number not known here reads "Other" with it, and any
    /// other text falls through as it is.
    /// </summary>
    public string TypeDisplay => RestorePointType switch
    {
        "0" or "APPLICATION_INSTALL" => "App install",
        "1" or "APPLICATION_UNINSTALL" => "App uninstall",
        // The point Windows makes as it restores, so that the restore can be undone.
        "6" or "RESTORE" => "Before a restore",
        // The point Windows makes on its own schedule.
        "7" or "CHECKPOINT" => "Automatic",
        "10" or "DEVICE_DRIVER_INSTALL" => "Driver install",
        "12" or "MODIFY_SETTINGS" => "Manual / settings",
        "13" or "CANCELLED_OPERATION" => "Cancelled operation",
        _ when uint.TryParse(RestorePointType, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            => $"Other ({RestorePointType})",
        _ => RestorePointType
    };
}
