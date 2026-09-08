// SysManager · ICleanupRoots
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// The directories Deep Cleanup's scan is built from. Injected so a test can point the scan at a tree
/// it created itself, instead of at whatever happens to be on the machine running the suite (#2176).
/// </summary>
/// <remarks>
/// <see cref="DeepCleanupService"/> asked the machine directly — <c>Environment.GetFolderPath</c> for
/// five special folders and <c>DriveInfo.GetDrives()</c> for the launcher probes — with no parameter to
/// redirect any of it. So its LOGIC was unassertable: a test could not say "a category over a folder
/// holding 3 files of 10 bytes reports 3 files and 30 bytes", because it could not create that folder
/// anywhere the scan would look. What was left were shapes that hold on any machine (the name is
/// non-empty, the size is non-negative), which pass for reasons nobody chose — and pass differently on a
/// fresh CI runner, where those caches are nearly all empty, than on a real workstation.
/// <para>Deliberately the ROOTS and not a filesystem abstraction. <c>CleanAsync</c> already takes the
/// paths it operates on, which is why its tests build real temp trees today and are the strongest in the
/// file. This gives <c>ScanAsync</c> the same property with the smallest possible seam: everything below
/// a root is still the real <see cref="System.IO.Directory"/>, so junction skipping, permission failures
/// and size aggregation are exercised for real rather than against a fake.</para>
/// </remarks>
public interface ICleanupRoots
{
    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    string LocalAppData { get; }

    /// <summary><c>%PROGRAMDATA%</c>.</summary>
    string ProgramData { get; }

    /// <summary>The drive the OS is installed on, with a trailing separator — e.g. <c>C:\</c>.</summary>
    string SystemDrive { get; }

    /// <summary>The Windows directory.</summary>
    string WindowsDirectory { get; }

    /// <summary>The per-user temp directory.</summary>
    string UserTemp { get; }

    /// <summary><c>%PROGRAMFILES(X86)%</c>.</summary>
    string ProgramFilesX86 { get; }

    /// <summary><c>%PROGRAMFILES%</c>.</summary>
    string ProgramFiles { get; }

    /// <summary>
    /// Roots of the fixed, ready drives, used to find game launchers installed off the system drive.
    /// </summary>
    /// <remarks>
    /// A property rather than a call to <c>DriveInfo.GetDrives()</c> at each use site: the Steam,
    /// shader-cache and Riot probes each enumerated drives separately, so a test that redirected only the
    /// special folders would still have walked the machine's real drive roots three times.
    /// <para>Read fresh each time by the real implementation, because the service that consumes it is a
    /// DI singleton — see <see cref="SystemCleanupRoots.FixedDriveRoots"/>.</para>
    /// </remarks>
    IReadOnlyList<string> FixedDriveRoots { get; }

    /// <summary>
    /// The current user's Recycle Bin folders, one per fixed drive.
    /// </summary>
    /// <remarks>
    /// Behind the seam for the same reason as the rest, and the omission was measurable rather than
    /// theoretical: with only the special folders redirected, the eleven deterministic scan tests took 48
    /// seconds, because every one of them still walked the real Recycle Bin on every drive of the machine
    /// running them. A "deterministic" test whose result depends on what is in the bin is not one.
    /// </remarks>
    IReadOnlyList<string> RecycleBinPaths { get; }
}

/// <summary>
/// The real machine's roots — exactly what <see cref="DeepCleanupService"/> read inline before the seam
/// existed, so the parameterless constructor behaves identically.
/// </summary>
public sealed class SystemCleanupRoots : ICleanupRoots
{
    public string LocalAppData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string ProgramData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>The drive the OS is installed on, with a trailing separator.</summary>
    /// <remarks>
    /// Derived from <c>Environment.SystemDirectory</c> rather than from <c>%SYSTEMDRIVE%</c>, and the
    /// <c>C:\</c> fallback is kept: <see cref="Path.GetPathRoot(string)"/> is documented as nullable and a
    /// null here would put every driver-leftover path at a relative location.
    /// </remarks>
    public string SystemDrive { get; } = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public string WindowsDirectory { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string UserTemp { get; } = Path.GetTempPath();

    public string ProgramFilesX86 { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    public string ProgramFiles { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>Roots of the fixed, ready drives, read from the machine.</summary>
    /// <remarks>
    /// Enumerated on every read, NOT cached in the constructor. <c>DeepCleanupService</c> is a DI
    /// singleton built once at startup, so caching here would freeze the drive list for the whole
    /// session — a drive plugged in or made ready afterwards would stop being scanned until restart.
    /// Every other member is a genuine constant for the process lifetime and is cached; this one is not.
    /// </remarks>
    public IReadOnlyList<string> FixedDriveRoots =>
        [.. DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)];

    /// <summary>The current user's Recycle Bin folders, read from the machine.</summary>
    /// <remarks>Read fresh for the same reason as <see cref="FixedDriveRoots"/> — it enumerates drives.</remarks>
    public IReadOnlyList<string> RecycleBinPaths => RecycleBinHelper.CurrentUserBinPaths();
}
