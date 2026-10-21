// SysManager · FileLocker
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

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

    public string StartTimeDisplay => StartTime is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    /// <summary>
    /// True for processes the Restart Manager flags as critical/system — terminating
    /// these is dangerous and the UI should warn or block.
    /// </summary>
    public bool IsCritical => AppType.Equals("RmCritical", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What kind of program this is, in plain words, for the Type column and the export. <see cref="AppType"/> keeps
    /// the Restart Manager's own name, which <see cref="IsCritical"/> compares, and the column showed it as it was:
    /// "RmExplorer", and "RmCritical" for the one process not to end (#2813).
    /// </summary>
    public string TypeDisplay => AppType.ToUpperInvariant() switch
    {
        "RMMAINWINDOW" => "App window",
        "RMOTHERWINDOW" => "Background window",
        "RMSERVICE" => "Windows service",
        "RMEXPLORER" => "File Explorer",
        "RMCONSOLE" => "Console app",
        "RMCRITICAL" => "Needed by Windows",
        _ => "Other",
    };
}
