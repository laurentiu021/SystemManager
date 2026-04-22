// SysManager · PowerShellOutput
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.Models;

public enum OutputKind { Info, Output, Warning, Error, Verbose, Debug, Progress }

public record PowerShellLine(OutputKind Kind, string Text, DateTime Timestamp)
{
    public static PowerShellLine Info(string text) => new(OutputKind.Info, text, DateTime.Now);
    public static PowerShellLine Output(string text) => new(OutputKind.Output, text, DateTime.Now);
    public static PowerShellLine Warn(string text) => new(OutputKind.Warning, text, DateTime.Now);
    public static PowerShellLine Err(string text) => new(OutputKind.Error, text, DateTime.Now);
}
