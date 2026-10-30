// SysManager · CommandLine — the program a command line starts, found the way Windows finds it
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Helpers;

/// <summary>
/// Reads the program out of a command line, the way <c>CreateProcess</c> does.
/// </summary>
/// <remarks>
/// Four parsers took the program out with <c>command.Trim('"', ' ')</c> and then cut at the first space when the result
/// was not a file. <c>Trim</c> takes off only the outer quotes, so for the usual form
/// <c>"C:\Program Files\VideoLAN\VLC\vlc.exe" --started-from-file "%1"</c> the closing quote of the path stayed, the
/// file was never found, and the cut landed inside the path: <c>C:\Program</c>. Right-click Menu then named every
/// verb's source "Program", and Startup Manager found no file to read a publisher or a signature from (#2708).
/// </remarks>
internal static class CommandLine
{
    // What CreateProcess and the shell will run without an extension being written.
    private static readonly string[] RunnableExtensions = [".exe", ".com", ".bat", ".cmd"];

    /// <summary>
    /// The program <paramref name="command"/> starts, with environment variables expanded. A quoted program runs to its
    /// closing quote. An unquoted one is tried at each space, left to right, as a file and then with a runnable
    /// extension added, and is otherwise the text up to the first space. Empty for a blank command line.
    /// </summary>
    /// <param name="fileExists">Whether a file is there; the file system unless a test passes its own.</param>
    internal static string Program(string? command, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";

        var exists = fileExists ?? File.Exists;
        var line = Environment.ExpandEnvironmentVariables(command).Trim();

        if (line.StartsWith('"'))
        {
            var closing = line.IndexOf('"', 1);
            return closing > 0 ? line[1..closing] : line[1..];
        }

        for (var space = line.IndexOf(' '); space > 0; space = line.IndexOf(' ', space + 1))
        {
            var candidate = line[..space];
            if (exists(candidate)) return candidate;
            if (!Path.HasExtension(candidate))
            {
                foreach (var extension in RunnableExtensions)
                {
                    if (exists(candidate + extension)) return candidate + extension;
                }
            }
        }

        if (exists(line)) return line;

        var first = line.IndexOf(' ');
        return first > 0 ? line[..first] : line;
    }
}
