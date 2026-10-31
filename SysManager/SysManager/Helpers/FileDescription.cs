// SysManager · FileDescription
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using Serilog;

namespace SysManager.Helpers;

/// <summary>The description in a running program's file, as Process Manager and Volume Control name it.</summary>
/// <remarks>
/// It is read from the file the program was started from. A program whose file was renamed or moved while it ran, a
/// self-updater swapping its exe or a portable app moved out of Downloads, threw <see cref="FileNotFoundException"/>
/// here, and the whole process list failed: Process Manager stayed empty for the session, and Refresh, the Dashboard's
/// "Why is it slow?" and Volume Control ended in the error dialog (#2718). Such a program has no description now.
/// </remarks>
internal static class FileDescription
{
    /// <summary>The description in the version resource of the file at <paramref name="path"/>.</summary>
    internal static string Read(string path) => FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";

    /// <summary>The description of the program at <paramref name="path"/>, or none when its file cannot be read.</summary>
    internal static string Of(string path) => Of(path, Read);

    /// <summary><see cref="Of(string)"/>, with the read given, so a test can make it fail.</summary>
    internal static string Of(string path, Func<string, string> read)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return read(path); }
        // FileNotFoundException for a file that is gone, and the rest of IOException for a drive that is.
        catch (IOException ex)
        {
            Log.Debug("No description for {Path}: {Error}", path, ex.Message);
            return "";
        }
    }
}
