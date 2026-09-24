// SysManager · RestorePointScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Runs the exact scripts <see cref="RestorePointService"/> builds in a real Windows PowerShell 5.1 — the engine an
/// elevated SysManager uses — with the restore-point cmdlets shadowed by local functions (#2436).
/// </summary>
/// <remarks>
/// A function takes precedence over a cmdlet of the same name, so each stub stands in for Windows' answer and System
/// Restore is never touched: nothing here needs elevation or changes the machine. What is under test is PowerShell's
/// own semantics, which a substituted runner cannot show. The bug was one of them: Windows reports its one-a-day limit
/// as a WARNING, <c>-ErrorAction Stop</c> ignores warnings, and so the script printed its success sentinel for a
/// restore point that was never made.
/// <para>The succeeding stubs are the positive controls: without them, a script that never printed its sentinel at all
/// would pass every refusal case. Every script starts with <see cref="WindowsPowerShellScript.StubsInEffect"/>, because
/// CI's runner is elevated: a stub that failed to shadow the real <c>Restore-Computer</c> there would restore it.</para>
/// </remarks>
public class RestorePointScriptTests
{
    private const string EnableStub = "function Enable-ComputerRestore { [CmdletBinding()] param($Drive) } ; ";

    // The refusal as Windows PowerShell 5.1 writes it (resource CannotCreateRestorePointWarning).
    private const string CheckpointRefusesForTheDay =
        "function Checkpoint-Computer { [CmdletBinding()] param($Description, $RestorePointType) " +
        "Write-Warning 'A new system restore point cannot be created because one has already been created within the past 1440 minutes.' } ; ";

    private const string CheckpointSucceeds =
        "function Checkpoint-Computer { [CmdletBinding()] param($Description, $RestorePointType) } ; ";

    private const string CheckpointFails =
        "function Checkpoint-Computer { [CmdletBinding()] param($Description, $RestorePointType) " +
        "throw 'System Restore is turned off for this drive.' } ; ";

    private static readonly string CreateGuard =
        WindowsPowerShellScript.StubsInEffect("Enable-ComputerRestore", "Checkpoint-Computer");

    private static readonly string RestoreGuard = WindowsPowerShellScript.StubsInEffect("Restore-Computer");

    [Fact]
    public async Task CreateScript_WhenWindowsDeclinesForTheDay_DoesNotConfirm()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            EnableStub + CheckpointRefusesForTheDay + CreateGuard + RestorePointService.BuildCreateScript("Probe"));

        Assert.DoesNotContain(RestorePointService.CreateOkSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateScript_WhenCheckpointFails_DoesNotConfirm()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            EnableStub + CheckpointFails + CreateGuard + RestorePointService.BuildCreateScript("Probe"));

        Assert.DoesNotContain(RestorePointService.CreateOkSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateScript_WhenWindowsMakesThePoint_Confirms()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            EnableStub + CheckpointSucceeds + CreateGuard + RestorePointService.BuildCreateScript("Probe"));

        Assert.Contains(RestorePointService.CreateOkSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreScript_WhenRestoreComputerFails_DoesNotConfirm()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            "function Restore-Computer { [CmdletBinding()] param($RestorePoint, [switch]$Confirm) " +
            "throw 'The restore point was not found.' } ; " + RestoreGuard + RestorePointService.BuildRestoreScript(42));

        Assert.DoesNotContain(RestorePointService.RestoreStartedSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreScript_WhenWindowsAcceptsTheRestore_Confirms()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            "function Restore-Computer { [CmdletBinding()] param($RestorePoint, [switch]$Confirm) } ; " +
            RestoreGuard + RestorePointService.BuildRestoreScript(42));

        Assert.Contains(RestorePointService.RestoreStartedSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStubGuard_RefusesToRunWhenAShadowIsMissing()
    {
        // Positive control for the guard itself: with no stub defined, the real cmdlet is what Get-Command finds,
        // and the script must stop before reaching the command. A guard that never threw would protect nothing.
        var (exitCode, output, _) = await WindowsPowerShellScript.RunAsync(RestoreGuard + "'REACHED'");

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("REACHED", output, StringComparison.Ordinal);
    }
}
