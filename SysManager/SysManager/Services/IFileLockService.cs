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
/// a real process (Gate-ARCH: system-mutating services are testable).
/// </summary>
public interface IFileLockService
{
    /// <summary>
    /// Returns the processes currently using <paramref name="path"/> (a file or folder).
    /// Empty when nothing holds it. Throws <see cref="ArgumentException"/> for bad input.
    /// </summary>
    IReadOnlyList<FileLocker> FindLockers(string path);

    /// <summary>
    /// Terminates the process with the given id. Returns true on success. Returns false
    /// (and logs) if the process is gone, access is denied (needs elevation), or it exits
    /// on its own. Callers must confirm with the user first.
    /// </summary>
    bool KillProcess(int processId);
}
