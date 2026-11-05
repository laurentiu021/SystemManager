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
/// <remarks>
/// One instance serves both tabs, so a call's progress goes to the reporter that call passes in. It went through an
/// event on the instance, which the Windows Update tab listened to for as long as it lived, so the Dashboard's check
/// wrote its lines into the tab's console, in the middle of an install log if one was running (#2791).
/// </remarks>
public interface IWindowsUpdateService
{
    /// <summary>
    /// Scans for updates that are not installed yet — everything Windows Update offers, optional drivers and
    /// feature upgrades included. Returns one entry per update, with a stable id for a later install.
    /// </summary>
    /// <param name="progress">
    /// Where this scan's progress lines go, or nowhere. Reported on a worker thread: the scan runs in <c>Task.Run</c>,
    /// so a reporter that touches the window marshals to its thread itself (#2765).
    /// </param>
    /// <exception cref="System.Runtime.InteropServices.COMException">The Windows Update Agent failed the search.</exception>
    /// <exception cref="UnauthorizedAccessException">The Windows Update Agent refused the search.</exception>
    Task<IReadOnlyList<UpdateEntry>> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Downloads and installs the given updates, setting each entry's status as it goes.
    /// </summary>
    /// <param name="progress">Where this install's progress lines go, or nowhere, on a worker thread too.</param>
    Task<InstallReport> InstallAsync(
        IReadOnlyList<UpdateEntry> entries, IProgress<string>? progress = null, CancellationToken ct = default);
}
