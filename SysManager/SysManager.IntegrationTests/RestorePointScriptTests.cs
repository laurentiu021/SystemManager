// SysManager · RestorePointScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Text;
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
/// would pass every refusal case.</para>
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

    [Fact]
    public async Task CreateScript_WhenWindowsDeclinesForTheDay_DoesNotConfirm()
    {
        var output = await RunWindowsPowerShellAsync(
            EnableStub + CheckpointRefusesForTheDay + RestorePointService.BuildCreateScript("Probe"));

        Assert.DoesNotContain(RestorePointService.CreateOkSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateScript_WhenCheckpointFails_DoesNotConfirm()
    {
        var output = await RunWindowsPowerShellAsync(
            EnableStub + CheckpointFails + RestorePointService.BuildCreateScript("Probe"));

        Assert.DoesNotContain(RestorePointService.CreateOkSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateScript_WhenWindowsMakesThePoint_Confirms()
    {
        var output = await RunWindowsPowerShellAsync(
            EnableStub + CheckpointSucceeds + RestorePointService.BuildCreateScript("Probe"));

        Assert.Contains(RestorePointService.CreateOkSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreScript_WhenRestoreComputerFails_DoesNotConfirm()
    {
        var output = await RunWindowsPowerShellAsync(
            "function Restore-Computer { [CmdletBinding()] param($RestorePoint, [switch]$Confirm) " +
            "throw 'The restore point was not found.' } ; " + RestorePointService.BuildRestoreScript(42));

        Assert.DoesNotContain(RestorePointService.RestoreStartedSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreScript_WhenWindowsAcceptsTheRestore_Confirms()
    {
        var output = await RunWindowsPowerShellAsync(
            "function Restore-Computer { [CmdletBinding()] param($RestorePoint, [switch]$Confirm) } ; " +
            RestorePointService.BuildRestoreScript(42));

        Assert.Contains(RestorePointService.RestoreStartedSentinel, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs <paramref name="script"/> in Windows PowerShell 5.1 and returns everything it wrote to standard output.
    /// Bounded: a PowerShell that never exits is killed and fails the test rather than hanging the suite.
    /// </summary>
    private static async Task<string> RunWindowsPowerShellAsync(string script)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            Assert.Skip("Windows PowerShell 5.1 is not present on this host.");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(powershell,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Windows PowerShell did not finish the script within 60 seconds.");
        }

        await stderr;
        return await stdout;
    }
}
