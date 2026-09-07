// SysManager · RecordingRunner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// An <see cref="IPowerShellRunner"/> that counts what it was asked to run, runs none of it, and can raise
/// its own output events on demand.
/// </summary>
/// <remarks>
/// Hand-rolled rather than substituted: this project does not reference NSubstitute, and pulling a second
/// mocking dependency in — plus the row it would need in TESTING.md's framework table — is a larger change
/// than the counter its callers actually need.
/// <para>Every run member increments the SAME <see cref="Calls"/> counter, because the assertions it serves
/// ask "did anything run at all", not which overload stayed untouched. <see cref="RaiseLine"/> and
/// <see cref="RaiseProgress"/> exist because two of the seams under test are pure pass-throughs
/// (<c>WingetService.LineReceived</c> forwards add/remove straight to the runner), and the only way to see a
/// pass-through work is to raise the far end and watch the near end fire.</para>
/// </remarks>
internal sealed class RecordingRunner : IPowerShellRunner
{
    /// <summary>How many times the view-model or service asked for something to be executed.</summary>
    public int Calls { get; private set; }

    public event Action<PowerShellLine>? LineReceived;

    public event Action<int>? ProgressChanged;

    /// <summary>Push a line through as the real runner's reader threads would.</summary>
    public void RaiseLine(PowerShellLine line) => LineReceived?.Invoke(line);

    /// <summary>Push a 0-100 progress reading through.</summary>
    public void RaiseProgress(int percent) => ProgressChanged?.Invoke(percent);

    public Task<Collection<PSObject>> RunAsync(string script,
                                               IDictionary<string, object?>? parameters = null,
                                               CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new Collection<PSObject>());
    }

    public Task<int> RunScriptViaPwshAsync(string script, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(0);
    }

    public Task<int> RunProcessAsync(string fileName, string arguments,
                                     CancellationToken cancellationToken = default,
                                     Encoding? outputEncoding = null)
    {
        Calls++;
        return Task.FromResult(0);
    }

    public Task<int> RunProcessWithShellAsync(string fileName, string arguments,
                                              CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(0);
    }
}
