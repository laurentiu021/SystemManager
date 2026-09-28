// SysManager · ProcessManagerKillTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Text;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// <see cref="ProcessManagerService.KillProcess"/> against real processes. What matters is what happens to the
/// processes around the one being ended, and only Windows can show that.
/// </summary>
/// <remarks>
/// The subject is a <c>cmd.exe</c> whose child is a Windows PowerShell that prints its own process ID and then
/// echoes each line it reads. The child shares the parent's redirected pipes. So the test learns the child's ID
/// without searching for it, and proves the child is alive by getting an answer from it. A dead child closes the
/// pipe, so the read returns end-of-stream at once rather than waiting out a timeout. Nothing sleeps.
/// <para>The child is held through a handle taken while it is known to be alive, so the clean-up cannot reach a
/// different process that was given the same ID later.</para>
/// </remarks>
public class ProcessManagerKillTests
{
    private const string EchoScript =
        "[Console]::Out.WriteLine($PID); [Console]::Out.Flush(); " +
        "while ($null -ne ($line = [Console]::In.ReadLine())) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }";

    /// <summary>
    /// Ending a process leaves the processes it started running (#2498).
    /// </summary>
    /// <remarks>
    /// It used to end the whole tree. Every program opened from the taskbar is a child of <c>explorer.exe</c>, so
    /// after SysManager's own administrator relaunch, ending Explorer closed all of them behind a prompt that named
    /// one process.
    /// </remarks>
    [Fact]
    public async Task KillProcess_EndsTheProcess_AndLeavesTheProcessItStartedRunning()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var parent = StartParentWithEchoingChild();
        using var child = await HoldChildAsync(parent, bounded.Token);

        try
        {
            var outcome = ProcessManagerService.KillProcess(parent.Id);

            Assert.Equal(ProcessManagerService.KillOutcome.Ended, outcome);
            await parent.WaitForExitAsync(bounded.Token);
            Assert.True(await ChildAnswersAsync(parent, bounded.Token),
                "ending the parent also ended the process it started");
        }
        finally
        {
            End(parent, child);
        }
    }

    /// <summary>
    /// A process whose start time is not the one listed is left alone: its ID has been given to another process.
    /// </summary>
    /// <remarks>
    /// The confirmation can stay open while the listed process exits and Windows reuses its ID, so the ID alone can
    /// name a different program by the time it is acted on. Driven here with a start time one second off, which is
    /// what a reused ID looks like from the list's side.
    /// </remarks>
    [Fact]
    public async Task KillProcess_StartTimeDoesNotMatch_LeavesTheProcessRunning()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var parent = StartParentWithEchoingChild();
        using var child = await HoldChildAsync(parent, bounded.Token);

        try
        {
            var outcome = ProcessManagerService.KillProcess(child.Id, child.StartTime.AddSeconds(-1));

            Assert.Equal(ProcessManagerService.KillOutcome.NotRunning, outcome);
            Assert.True(await ChildAnswersAsync(parent, bounded.Token),
                "a process whose start time did not match was ended anyway");
        }
        finally
        {
            End(parent, child);
        }
    }

    private static Process StartParentWithEchoingChild()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            Assert.Skip("Windows PowerShell 5.1 is not present on this host.");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(EchoScript));
        return Process.Start(new ProcessStartInfo("cmd.exe",
            $"/d /c {powershell} -NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
    }

    private static async Task<Process> HoldChildAsync(Process parent, CancellationToken ct)
    {
        var line = await parent.StandardOutput.ReadLineAsync(ct);
        Assert.True(int.TryParse(line, out var pid), $"the child did not report its process ID (read '{line}')");

        // Taken while the child is known to be alive; reading Handle keeps it open for the object's lifetime.
        var child = Process.GetProcessById(pid);
        _ = child.Handle;
        return child;
    }

    private static async Task<bool> ChildAnswersAsync(Process parent, CancellationToken ct)
    {
        await parent.StandardInput.WriteLineAsync("still-here".AsMemory(), ct);
        await parent.StandardInput.FlushAsync(ct);
        return await parent.StandardOutput.ReadLineAsync(ct) == "still-here";
    }

    private static void End(Process parent, Process child)
    {
        // Closing the pipe ends the child's read loop, and the parent exits with it; killing covers a stuck one.
        try { parent.StandardInput.Close(); }
        catch (IOException) { /* the pipe is already broken */ }

        foreach (var process in new[] { child, parent })
        {
            try { if (!process.WaitForExit(TimeSpan.FromSeconds(10))) process.Kill(); }
            catch (InvalidOperationException) { /* already gone */ }
        }
    }
}
