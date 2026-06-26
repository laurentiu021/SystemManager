// SysManager · FileLocker
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A single process that the Windows Restart Manager reports as holding a lock on
/// (or otherwise using) a queried file or folder.
/// </summary>
public sealed record FileLocker(
    int ProcessId,
    string ProcessName,
    string AppType,
    DateTime? StartTime)
{
    /// <summary>Friendly label, e.g. "explorer.exe (12345)".</summary>
    public string Display => $"{ProcessName} ({ProcessId})";

    public string StartTimeDisplay => StartTime is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "—";

    /// <summary>
    /// True for processes the Restart Manager flags as critical/system — terminating
    /// these is dangerous and the UI should warn or block.
    /// </summary>
    public bool IsCritical => AppType.Equals("RmCritical", StringComparison.OrdinalIgnoreCase);
}
