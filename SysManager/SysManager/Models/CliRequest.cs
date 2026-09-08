// SysManager · CliRequest — a parsed command-line invocation
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>The headless command requested on the command line.</summary>
public enum CliCommand
{
    /// <summary>No CLI verb present — launch the normal GUI.</summary>
    None,
    Help,
    Version,
    List,
    Health,
    Cleanup,
    /// <summary>
    /// Purge the standby memory list (<c>--purge-standby</c>, or the retained <c>--trim-ram</c> alias).
    /// </summary>
    /// <remarks>
    /// Was <c>TrimRam</c>, which named a DIFFERENT operation. Performance Mode's "Trim RAM" calls
    /// <c>EmptyWorkingSet</c> per process and needs no elevation; this calls
    /// <c>NtSetSystemInformation(MemoryPurgeStandbyList)</c> and needs administrator. They are
    /// complementary ends of the same subject, not duplicates — so a user who scheduled "TrimRam" got a
    /// different operation with a different elevation requirement from the button of that name (#1524).
    /// </remarks>
    PurgeStandby,
    /// <summary>A <c>--flag</c> that isn't a recognized verb — usage error.</summary>
    Unknown,
}

/// <summary>
/// A parsed CLI invocation: the verb plus output modifiers. Produced by the pure
/// <see cref="Services.CliRunner.Parse"/>; carries no behavior of its own.
/// </summary>
public sealed record CliRequest(CliCommand Command, bool Json = false, bool Silent = false, string? UnknownArg = null);

/// <summary>The outcome of running a CLI command: a process exit code and the text to print.</summary>
public sealed record CliResult(int ExitCode, string Output)
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int UsageError = 2;
}
