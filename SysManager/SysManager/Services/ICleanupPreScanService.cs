// SysManager · ICleanupPreScanService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>The two headline numbers Quick Cleanup shows before the user asks for anything.</summary>
/// <param name="TempLabel">
/// A finished, user-facing phrase for the temp folders — "412.7 MB can be freed", "Empty", or
/// "Unable to scan".
/// </param>
/// <param name="RecycleBinLabel">The same for the current user's Recycle Bin.</param>
public sealed record CleanupPreScan(string TempLabel, string RecycleBinLabel);

/// <summary>
/// Measures what the temp folders and the Recycle Bin currently hold.
/// </summary>
/// <remarks>
/// A seam, and it exists for a reason beyond tidiness. This work used to sit inline in
/// <c>CleanupViewModel.PreScanAsync</c>, which the constructor fires and forgets — so building the view-model
/// started a recursive walk of both temp folders and every per-SID Recycle Bin folder. In the unit suite that
/// happened 30 times in one file, and one test asserted that the walk finished within fifteen seconds, which
/// is a claim about the machine rather than about the code. It failed on a normally-used desktop while passing
/// on CI, where the profile is nearly empty.
/// <para>Behind an interface, a test states what the numbers are and never touches a disk.</para>
/// </remarks>
public interface ICleanupPreScanService
{
    /// <summary>
    /// Measures both locations off the calling thread. Never throws for an inaccessible file or folder —
    /// individual failures are skipped, and a wholesale failure yields "Unable to scan" for that location, so
    /// a caller always gets two displayable phrases.
    /// </summary>
    Task<CleanupPreScan> MeasureAsync();
}
