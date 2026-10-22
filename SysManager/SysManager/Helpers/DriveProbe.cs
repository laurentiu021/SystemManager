// SysManager · DriveProbe
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// One drive as <see cref="DriveInfo"/> answers for it, behind delegates, so a test can stand in a drive that is not
/// ready or that refuses to be read.
/// </summary>
/// <remarks>
/// Only <see cref="Type"/> is read up front: <see cref="DriveInfo.DriveType"/> does not touch the drive. Everything
/// else is asked when called.
/// </remarks>
internal sealed record DriveProbe(
    string Name,
    DriveType Type,
    Func<bool> IsReady,
    Func<long> TotalSize,
    Func<long> FreeSpace,
    Func<string> Format)
{
    /// <summary>The probe for a real drive.</summary>
    public static DriveProbe Of(DriveInfo drive) => new(
        drive.Name,
        drive.DriveType,
        () => drive.IsReady,
        () => drive.TotalSize,
        () => drive.AvailableFreeSpace,
        () => drive.DriveFormat);

    /// <summary>
    /// <paramref name="read"/> of each fixed drive that is ready, in order. A drive that cannot be read is left out
    /// and the rest are still read.
    /// </summary>
    /// <remarks>
    /// The type is asked first. <see cref="DriveInfo.IsReady"/> opens the drive's root, which waits on a mapped drive
    /// whose server cannot be reached or an optical drive spinning up, and the Dashboard's storage card asked it of
    /// every letter before the type ruled one out (#2802).
    /// <para>The sizes and the format are read from the drive, and a drive that goes away or refuses them throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>. Each drive is caught on its own: the
    /// Dashboard's card and About's environment info held all of them in one <c>try</c>, so the first drive that
    /// threw ended the list, and the card caught only the first of the two, so a refusal stopped the Dashboard's
    /// start. <see cref="Services.FixedDriveService"/> was fixed for the same thing earlier.</para>
    /// </remarks>
    public static List<T> ReadFixed<T>(IEnumerable<DriveProbe> drives, Func<DriveProbe, T> read)
    {
        List<T> results = [];
        foreach (var drive in drives)
        {
            if (drive.Type != DriveType.Fixed) continue;
            try
            {
                if (drive.IsReady()) results.Add(read(drive));
            }
            catch (IOException ex)
            {
                Log.Debug("Drive {Drive} could not be read: {Error}", drive.Name, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Debug("Drive {Drive} refused: {Error}", drive.Name, ex.Message);
            }
        }
        return results;
    }
}
