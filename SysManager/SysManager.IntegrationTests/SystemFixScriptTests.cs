// SysManager · SystemFixScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Runs <see cref="SystemFixService.ResetWindowsUpdateScript"/> in a real Windows PowerShell 5.1 with every cmdlet
/// that would change the machine shadowed by a function, so the renames and the service restarts are observed, not
/// performed (#2439).
/// </summary>
/// <remarks>
/// The shipped script overrode its own <c>$ErrorActionPreference = 'Stop'</c> with <c>-ErrorAction SilentlyContinue</c>
/// on both renames. With a rename failing the way a folder still held open fails, it exited 0 and printed "Windows
/// Update components reset" — reproduced before the fix with these same stubs. <c>RunFixAsync</c> turns a terminating
/// error into <c>Success: false</c>, so a non-zero exit here is what the tab then reports as a failure.
/// </remarks>
public class SystemFixScriptTests
{
    private const string SuccessSentence = "Windows Update components reset";

    // The machine-changing commands, each shadowed: stopping and starting only write what they were asked.
    private const string ServiceStubs =
        "function Stop-Service  { [CmdletBinding()] param($Name, [switch]$Force) \"STOPPED:$Name\" } ; " +
        "function Start-Service { [CmdletBinding()] param($Name) \"STARTED:$Name\" } ; " +
        "function Test-Path     { param($Path) $true } ; ";

    // Starts with a short token because PowerShell wraps error text to a console width: a sentence can be split.
    private const string RenameFailsAsIfInUse =
        "function Rename-Item { [CmdletBinding()] param($Path, $NewName) " +
        "Write-Error \"RENAMEREFUSED: the process cannot access '$Path' because it is being used by another process.\" } ; ";

    private const string RenameSucceeds =
        "function Rename-Item { [CmdletBinding()] param($Path, $NewName) \"RENAMED:$Path\" } ; ";

    private static readonly string Guard =
        WindowsPowerShellScript.StubsInEffect("Stop-Service", "Start-Service", "Test-Path", "Rename-Item");

    [Fact]
    public async Task WhenAFolderCannotBeRenamed_TheResetFails_AndTheServicesStillRestart()
    {
        var (exitCode, output, error) = await WindowsPowerShellScript.RunAsync(
            ServiceStubs + RenameFailsAsIfInUse + Guard + SystemFixService.ResetWindowsUpdateScript);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain(SuccessSentence, output, StringComparison.Ordinal);
        // Surfaced rather than swallowed: the shipped script's SilentlyContinue left the error stream empty.
        Assert.Contains("RENAMEREFUSED", error, StringComparison.Ordinal);
        // The finally: a failed rename must not leave Windows Update stopped.
        foreach (var service in new[] { "wuauserv", "cryptSvc", "bits", "msiserver" })
            Assert.Contains($"STARTED:{service}", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenBothFoldersAreRenamed_TheResetReportsSuccess()
    {
        // The positive control: the same harness sees the success sentence when nothing fails, so the test above
        // is not passing because the script never prints it.
        var (exitCode, output, _) = await WindowsPowerShellScript.RunAsync(
            ServiceStubs + RenameSucceeds + Guard + SystemFixService.ResetWindowsUpdateScript);

        Assert.Equal(0, exitCode);
        Assert.Contains(SuccessSentence, output, StringComparison.Ordinal);
        Assert.Contains("SoftwareDistribution", output, StringComparison.Ordinal);
        Assert.Contains("catroot2", output, StringComparison.Ordinal);
    }
}
