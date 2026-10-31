// SysManager · TempCleanResult — what one sweep of the temp folders removed, and whether it was cancelled part-way
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// What one sweep of the temp folders removed: the bytes and the files deleted, the files it could not delete, and
/// whether it was cancelled part-way.
/// </summary>
/// <remarks>
/// A cancelled sweep used to throw, and the count of what it had already deleted went with the exception, so Quick
/// Cleanup, the Dashboard's Quick Cleanup and the Quick Tune-Up said only that it was cancelled and recorded nothing. The
/// files are gone for good either way, so the counts come back with <see cref="Cancelled"/> set instead (#2736).
/// </remarks>
/// <param name="BytesFreed">The size of the files deleted.</param>
/// <param name="FilesDeleted">How many files were deleted.</param>
/// <param name="Errors">How many files were in use or protected, and were left.</param>
/// <param name="Cancelled">True when the sweep was cancelled before it finished.</param>
public sealed record TempCleanResult(long BytesFreed, int FilesDeleted, int Errors, bool Cancelled);
