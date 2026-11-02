// SysManager · IPowerShellRunner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction over <see cref="PowerShellRunner"/> — the single seam through which
/// services run PowerShell scripts and external processes. Extracting this interface
/// lets system-mutating services (DNS, network repair, winget install) be unit-tested
/// with a substituted runner instead of touching the live OS (the rule: "external
/// process/PowerShell calls route through the single runner seam").
///
/// <para>The same <b>SECURITY CONTRACT</b> as <see cref="PowerShellRunner"/> applies:
/// callers MUST only pass hard-coded script strings to <see cref="RunAsync"/> and
/// <see cref="RunScriptViaPwshAsync"/>. User input MUST NEVER be interpolated into
/// scripts.</para>
/// </summary>
public interface IPowerShellRunner
{
    /// <summary>
    /// Raised for each line of output from any stream. Fires on a thread-pool thread —
    /// subscribers that update UI elements must marshal to the dispatcher.
    /// </summary>
    event Action<PowerShellLine>? LineReceived;

    /// <summary>Raised with a 0-100 percentage as PowerShell progress records arrive.</summary>
    event Action<int>? ProgressChanged;

    /// <summary>
    /// Execute a script and return the collected PSObject results.
    /// Execution uses Windows PowerShell 5.1, elevated or not, and crosses
    /// an out-of-process remoting boundary. Scripts must remain PowerShell 5.1-compatible,
    /// project complex results to explicit properties, and callers must not rely on live
    /// object identity or methods after deserialization.
    /// All streams are forwarded via <see cref="LineReceived"/> for live UI display.
    /// </summary>
    Task<Collection<PSObject>> RunAsync(
        string script,
        IDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run a PowerShell script via an external powershell.exe (Windows PS 5.1),
    /// returning the process exit code. Output is streamed via <see cref="LineReceived"/>.
    /// </summary>
    Task<int> RunScriptViaPwshAsync(
        string script,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run an external process (winget, netsh, ipconfig, …) with live line streaming,
    /// returning the process exit code.
    /// </summary>
    Task<int> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default,
        Encoding? outputEncoding = null);

    /// <summary>
    /// Run an external process as <see cref="RunProcessAsync"/> does, and return its exit code with the lines that
    /// process wrote to standard output. Every line is still raised on <see cref="LineReceived"/> for live display.
    /// </summary>
    /// <remarks>
    /// For a caller that reads what the program printed. <see cref="LineReceived"/> carries every run on this runner, so
    /// a caller that collected from it while its own run was going also kept the lines of any run beside it: a search
    /// during the installed list read both tables (#2771). The lines returned here come from this process only.
    /// </remarks>
    Task<ProcessOutput> RunProcessForOutputAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default,
        Encoding? outputEncoding = null);

    /// <summary>
    /// Run a validated executable through the Windows shell so its own manifest can
    /// request elevation. Use only after the caller has validated both the executable
    /// and arguments at its trust boundary.
    /// </summary>
    Task<int> RunProcessWithShellAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default);
}
