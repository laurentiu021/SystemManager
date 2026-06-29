// SysManager · WingetResult
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// The outcome of a winget operation, translated from the raw process exit code into a
/// human-readable result so the UI never has to surface a bare numeric code.
/// </summary>
public sealed record WingetResult(int ExitCode, bool Succeeded, string FriendlyMessage)
{
    public static WingetResult From(int exitCode) =>
        new(exitCode, exitCode == 0, WingetExitCodes.Describe(exitCode));

    /// <summary>A cancelled operation (no meaningful exit code).</summary>
    public static WingetResult Cancelled { get; } = new(-1, false, "Cancelled");
}

/// <summary>
/// Maps the documented winget (APPINSTALLER_CLI_ERROR_*) exit codes to short, friendly
/// English. Unknown non-zero codes fall back to a hex form. Pure and unit-testable.
/// </summary>
public static class WingetExitCodes
{
    public static string Describe(int exitCode) => unchecked((uint)exitCode) switch
    {
        0x00000000 => "Updated",
        0x8A150011 => "No applicable update found",
        0x8A15002B => "No applicable update found",
        0x8A150109 => "Update installed — restart required",
        0x8A150049 => "Another install is in progress — try again shortly",
        0x8A150019 => "Cancelled",
        0x8A15010C => "App is running — close it and retry",
        0x8A150056 => "Installer failed — see the log",
        0x8A150010 => "Couldn't find the app in the catalog",
        0x8A150047 => "Network error reaching the source",
        _ => $"Failed (winget code 0x{unchecked((uint)exitCode):X8})",
    };
}
