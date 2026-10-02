// SysManager · IFileLockService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="FileLockService"/> — identifies which processes are
/// holding a lock on a file/folder via the Windows Restart Manager, and terminates a
/// locker. Extracting this interface lets <c>FileLockViewModel</c>'s mutating command
/// path (KillSelected) be unit-tested with a substituted service instead of terminating
/// a real process (system-mutating services are testable).
/// </summary>
public interface IFileLockService
{
    /// <summary>
    /// Returns the processes currently using <paramref name="path"/>. For a folder, that means the files inside it.
    /// Null when Restart Manager could not complete the check. Throws <see cref="ArgumentException"/> for an empty
    /// path and <see cref="System.IO.FileNotFoundException"/> when nothing exists at it.
    /// </summary>
    FileLockScan? FindLockers(string path);

    /// <summary>
    /// Ends the process with <paramref name="processId"/>, and only if it is the locker that was listed: a process
    /// with that ID that started at another time is left alone and reported as
    /// <see cref="ProcessManagerService.KillOutcome.NotRunning"/>. Callers must confirm with the user first.
    /// </summary>
    /// <param name="processId">The locker's process ID.</param>
    /// <param name="startTime">
    /// When the locker started, as Restart Manager reported it, or null when it did not say, which skips the check.
    /// Windows gives a closed process's ID to the next one started, so an ID from a list can name a different
    /// program (#2514).
    /// </param>
    ProcessManagerService.KillOutcome KillProcess(int processId, DateTime? startTime);
}
