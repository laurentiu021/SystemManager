// SysManager · IUndoChangesService — finds the changes SysManager can put back, and puts one back
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Finds the changes SysManager can put back, and puts one back at a time (#1525).
/// </summary>
/// <remarks>
/// Owns no file. Every change it lists is read from a copy one tab already keeps to undo its own change, and every
/// put-back goes through the same restore that tab uses, so there is one way to undo each change, wherever it is
/// started from.
/// </remarks>
public interface IUndoChangesService
{
    /// <summary>
    /// Reads every kept copy and describes what each would put back now. A copy that could not be used is named in
    /// <see cref="UndoScan.Problems"/> instead of being described as nothing to do.
    /// </summary>
    Task<UndoScan> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Asks Windows for its newest restore point. Apart from the scan, because the question runs through the PowerShell
    /// runner that creating a restore point holds for as long as that takes, and the changes must not wait for it.
    /// </summary>
    Task<RestorePointLook> LookForRestorePointAsync(CancellationToken ct = default);

    /// <summary>
    /// Puts <paramref name="change"/> back, after reading its copy again: what is there now must still be what the
    /// question described, or nothing changes and the outcome says so.
    /// </summary>
    Task<UndoOutcome> PutBackAsync(UndoChange change, CancellationToken ct = default);
}
