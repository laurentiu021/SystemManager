// SysManager · PowerShellOutput
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

public enum OutputKind { Info, Output, Warning, Error, Verbose, Debug, Progress }

public sealed record PowerShellLine(OutputKind Kind, string Text, DateTime Timestamp)
{
    public static PowerShellLine Info(string text) => new(OutputKind.Info, text, DateTime.Now);
    public static PowerShellLine Output(string text) => new(OutputKind.Output, text, DateTime.Now);
    public static PowerShellLine Warn(string text) => new(OutputKind.Warning, text, DateTime.Now);
    public static PowerShellLine Err(string text) => new(OutputKind.Error, text, DateTime.Now);

    /// <summary>
    /// The line's text, which a screen reader says as the live output is arrowed through. The record's own read
    /// "PowerShellLine { Kind = Output, Text = …, Timestamp = … }" for every line (#2785).
    /// </summary>
    public override string ToString() => Text;
}

/// <summary>
/// What one external process wrote to its standard output, and how it exited: only that process's lines, whatever else
/// runs on the same runner meanwhile (#2771).
/// </summary>
public sealed record ProcessOutput(int ExitCode, IReadOnlyList<string> Lines);
