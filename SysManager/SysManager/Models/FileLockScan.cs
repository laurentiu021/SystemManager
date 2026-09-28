// SysManager · FileLockScan
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// What one File Lock Detector check found: the processes using the path, and what was checked to find them.
/// </summary>
/// <param name="Lockers">The processes Restart Manager reports as using the file, or any file checked in the folder.</param>
/// <param name="IsFolder">True when the path was a folder, so the check covered the files inside it.</param>
/// <param name="FilesChecked">How many files were checked: one for a file, the files found for a folder.</param>
/// <param name="CheckedOnlyPart">
/// True when the folder held more files than one check covers, so only the first
/// <c>FileLockService.MaxFolderFiles</c> were checked.
/// </param>
public sealed record FileLockScan(
    IReadOnlyList<FileLocker> Lockers,
    bool IsFolder,
    int FilesChecked,
    bool CheckedOnlyPart);
