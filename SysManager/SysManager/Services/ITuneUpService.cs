// SysManager · ITuneUpService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// The Dashboard's two cleaners, behind a seam: the full Quick Tune-Up, and the temp sweep that Quick Cleanup
/// runs on its own.
/// </summary>
/// <remarks>
/// Both delete files for real, and the Tune-Up also empties the Recycle Bin. Behind the seam, a view model
/// tested against a substitute can run either quick action without touching the machine it runs on.
/// </remarks>
public interface ITuneUpService
{
    /// <summary>Runs the full tune-up sequence, reporting progress for each step.</summary>
    Task<TuneUpResult> RunAsync(
        bool emptyRecycleBin, IProgress<(int Step, string Message)>? progress = null, CancellationToken ct = default);

    /// <summary>Cleans the user and Windows TEMP folders, and returns what was freed.</summary>
    Task<(long BytesFreed, int FilesDeleted, int Errors)> CleanTempFilesAsync(CancellationToken ct = default);
}
