// SysManager · KeptCopy
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Keeps a previous build in an updates folder the way an update does: the binary, its checksum, and the versions
/// record naming the version that update installed.
/// </summary>
/// <remarks>
/// Go back is offered only when all three are there and the record names the running version, so a test that wrote
/// the binary and its checksum alone set up a copy no update would leave, and one Go back does not offer (#2725).
/// </remarks>
internal static class KeptCopy
{
    /// <summary>The kept build's own version in these tests.</summary>
    public static readonly Version Version = new(1, 0, 0);

    /// <summary>Keeps <paramref name="content"/> as the previous build in <paramref name="updatesDir"/>.</summary>
    /// <param name="installedBy">The version the update that kept it installed; the running one unless a test says otherwise.</param>
    public static void Keep(string updatesDir, string content = "OLD-BUILD", Version? installedBy = null)
    {
        Directory.CreateDirectory(updatesDir);
        var previous = UpdateApplier.PreviousBuildPath(updatesDir);
        File.WriteAllText(previous, content);
        File.WriteAllText(UpdateApplier.PreviousBuildHashPath(updatesDir), UpdateApplier.ComputeFileHash(previous));
        File.WriteAllText(UpdateApplier.PreviousBuildVersionsPath(updatesDir),
            UpdateApplier.FormatKeptCopyVersions(Version, installedBy ?? UpdateService.CurrentVersion));
    }
}
