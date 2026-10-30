// SysManager · SystemFixScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Runs <see cref="SystemFixService.ResetWindowsUpdateScript"/> in a real Windows PowerShell 5.1 with every cmdlet
/// that would change the machine shadowed by a function, so the renames and the service restarts are observed, not
/// performed (#2439). <see cref="SystemFixService.ReinstallWinGetScript"/> runs the same way, with the App Installer
/// packages it is shown chosen by the test and its registration observed (#2835).
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

    /// <summary>
    /// Cancel reaches this script by stopping the PowerShell pipeline, which can land anywhere — including
    /// partway through the stop loop itself, the wait being longest exactly when Windows Update is stuck
    /// (#2789). Simulated here as a hard <c>exit</c> right after the second service's own
    /// <c>Stop-Service</c> call, standing in for the pipeline being torn down right after the first two
    /// services stopped but before the third and fourth were reached.
    /// </summary>
    /// <remarks>
    /// A stub that instead <c>throw</c>s does NOT work here — measured, not assumed: the product script
    /// calls <c>Stop-Service -Name $s -Force -ErrorAction SilentlyContinue</c>, and probed directly in
    /// Windows PowerShell, <c>-ErrorAction SilentlyContinue</c> on the call site swallows a bare
    /// <c>throw</c> from inside an advanced ([CmdletBinding()]) function entirely — execution continues
    /// past it as if nothing happened, so a reverted (unfixed) script would pass this test too. The REAL
    /// mechanism, <c>PowerShellRunner</c>'s <c>ps.Stop()</c> on cancellation, is a hard pipeline abort, not
    /// a script-level error subject to <c>-ErrorAction</c> at all — <c>exit</c> is the closest a plain
    /// script string can get to that: it unwinds through an enclosing <c>try</c>/<c>finally</c> exactly
    /// like a real pipeline stop does, also confirmed directly, rather than being suppressed like a throw.
    /// </remarks>
    private const string StopExitsRightAfterTheSecondService =
        "function Stop-Service { [CmdletBinding()] param($Name, [switch]$Force) \"STOPPED:$Name\" ; " +
        "if ($Name -eq 'cryptSvc') { exit 7 } } ; " +
        "function Start-Service { [CmdletBinding()] param($Name) \"STARTED:$Name\" } ; " +
        "function Test-Path     { param($Path) $true } ; ";

    [Fact]
    public async Task WhenTheStopLoopItselfIsInterrupted_TheServicesAlreadyStoppedStillRestart()
    {
        var (exitCode, output, _) = await WindowsPowerShellScript.RunAsync(
            StopExitsRightAfterTheSecondService + RenameSucceeds + Guard + SystemFixService.ResetWindowsUpdateScript);

        Assert.Equal(7, exitCode);
        Assert.DoesNotContain(SuccessSentence, output, StringComparison.Ordinal);
        // The two services that stopped before the interruption — and every other named service, since the
        // finally loop restarts all four unconditionally — must still be started again. Before the fix,
        // this exit happened outside any try block, so the finally below was never entered at all and
        // wuauserv/cryptSvc stayed stopped.
        foreach (var service in new[] { "wuauserv", "cryptSvc", "bits", "msiserver" })
            Assert.Contains($"STARTED:{service}", output, StringComparison.Ordinal);
        // Neither rename ran: the interruption happened before the try's original body was ever reached.
        Assert.DoesNotContain("RENAMED:", output, StringComparison.Ordinal);
    }

    // ---------- Reinstall WinGet (#2835) ----------

    // Registering only writes the manifest it was given.
    private const string RegisterStub =
        "function Add-AppxPackage { [CmdletBinding()] param([switch]$DisableDevelopmentMode, [switch]$Register, " +
        "[Parameter(Position = 0)]$Path) \"REGISTERED:$Path\" } ; ";

    private static readonly string AppxGuard =
        WindowsPowerShellScript.StubsInEffect("Get-AppxPackage", "Add-AppxPackage");

    /// <summary>
    /// <c>Get-AppxPackage</c> as Windows answers it: the user's own App Installer, if any, and with <c>-AllUsers</c>
    /// one for every version registered on the PC, in the order given.
    /// </summary>
    private static string Packages(string? mine, params string[] everyone)
    {
        static string Package(string version) =>
            $"[pscustomobject]@{{ Name = 'Microsoft.DesktopAppInstaller'; Version = '{version}'; " +
            $"InstallLocation = 'C:\\Apps\\AppInstaller_{version}' }}";
        return "function Get-AppxPackage { [CmdletBinding()] " +
               "param([Parameter(Position = 0)]$Name, [switch]$AllUsers) " +
               $"if ($AllUsers) {{ {string.Join(", ", everyone.Select(Package))} }} " +
               $"else {{ {(mine is null ? "" : Package(mine))} }} }} ; ";
    }

    private static string ManifestOf(string version) => $@"REGISTERED:C:\Apps\AppInstaller_{version}\AppXManifest.xml";

    [Fact]
    public async Task ReinstallWinGet_RegistersTheUsersOwnAppInstaller_NotAnOlderOneListedFirst()
    {
        // With a second account, -AllUsers can list an older App Installer still registered for it first, and the
        // script registered whichever came first: Windows refuses an older one while a newer one is registered.
        var (exitCode, output, _) = await WindowsPowerShellScript.RunAsync(
            Packages("1.29.380.0", "1.21.3482.0", "1.29.380.0") + RegisterStub + AppxGuard
            + SystemFixService.ReinstallWinGetScript);

        Assert.Equal(0, exitCode);
        Assert.Contains(ManifestOf("1.29.380.0"), output, StringComparison.Ordinal);
        Assert.DoesNotContain("1.21.3482.0", output, StringComparison.Ordinal);
        Assert.Contains("re-registered successfully", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReinstallWinGet_WithNoneForTheUser_RegistersTheNewestOnThePC()
    {
        // By version, not by text, which puts 1.9 after 1.29.
        var (exitCode, output, _) = await WindowsPowerShellScript.RunAsync(
            Packages(mine: null, "1.9.25200.0", "1.29.380.0", "1.21.3482.0") + RegisterStub + AppxGuard
            + SystemFixService.ReinstallWinGetScript);

        Assert.Equal(0, exitCode);
        Assert.Contains(ManifestOf("1.29.380.0"), output, StringComparison.Ordinal);
        Assert.Single(output.Split('\n'), line => line.StartsWith("REGISTERED:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReinstallWinGet_WithNoAppInstaller_SaysToInstallIt_AndRegistersNothing()
    {
        var (exitCode, output, error) = await WindowsPowerShellScript.RunAsync(
            Packages(mine: null) + RegisterStub + AppxGuard + SystemFixService.ReinstallWinGetScript);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("REGISTERED:", output, StringComparison.Ordinal);
        Assert.Contains("package not found", error, StringComparison.Ordinal);
    }
}
