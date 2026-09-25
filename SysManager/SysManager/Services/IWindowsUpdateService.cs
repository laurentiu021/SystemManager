// SysManager · IWindowsUpdateService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// The Windows Update Agent, behind a seam. Two tabs use it — Windows Update, which scans and installs, and
/// the Dashboard, which only scans — and a view model tested against a substitute never reaches the COM API,
/// which searches Microsoft's servers for real and can take minutes.
/// </summary>
public interface IWindowsUpdateService
{
    /// <summary>Raised on the calling thread for each progress/log line.</summary>
    event Action<string>? Log;

    /// <summary>
    /// Scans for updates that are not installed yet — everything Windows Update offers, optional drivers and
    /// feature upgrades included. Returns one entry per update, with a stable id for a later install.
    /// </summary>
    /// <exception cref="System.Runtime.InteropServices.COMException">The Windows Update Agent failed the search.</exception>
    /// <exception cref="UnauthorizedAccessException">The Windows Update Agent refused the search.</exception>
    Task<IReadOnlyList<UpdateEntry>> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads and installs the given updates, setting each entry's status as it goes.
    /// </summary>
    Task<InstallReport> InstallAsync(IReadOnlyList<UpdateEntry> entries, CancellationToken ct = default);
}
