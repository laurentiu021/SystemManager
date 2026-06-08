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
/// with a substituted runner instead of touching the live OS (Gate-ARCH: "external
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
    /// Execute a script in-process and return the collected PSObject results.
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
}
