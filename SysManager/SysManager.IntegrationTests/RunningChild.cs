// SysManager · RunningChild
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Text;

namespace SysManager.IntegrationTests;

/// <summary>
/// A Windows PowerShell that a test starts and owns, for tests that end or change a real process without touching
/// anything outside the test. It can hold a file open, and it answers on its pipes, so a test can tell it is alive
/// without sleeping.
/// </summary>
/// <remarks>
/// Started by this process, so <see cref="Process"/> keeps a handle to it until <see cref="Dispose"/>. Windows does
/// not give a process's ID to another while a handle to it is open, so the clean-up cannot reach a different process
/// that was given the same ID later.
/// </remarks>
internal sealed class RunningChild : IDisposable
{
    // Opens the file named by the variable, when there is one, then says it is ready, then echoes each line it reads
    // until its input closes. Passed through the environment rather than the script, so no path needs quoting. A file
    // it cannot open stops it before it says it is ready, so StartAsync fails there rather than a later assertion.
    private const string HeldFileVariable = "SYSMANAGER_TEST_HELD_FILE";

    private const string Script =
        "$ErrorActionPreference = 'Stop'; " +
        "if ($env:SYSMANAGER_TEST_HELD_FILE) { $held = [IO.File]::Open($env:SYSMANAGER_TEST_HELD_FILE, 'Open', 'Read', 'None') }; " +
        "[Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); " +
        "while ($null -ne ($line = [Console]::In.ReadLine())) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }";

    private RunningChild(Process process) => Process = process;

    public Process Process { get; }

    public int Id => Process.Id;

    public DateTime StartTime => Process.StartTime;

    /// <summary>
    /// Starts the child and returns once it is answering, holding <paramref name="heldFile"/> when one is given.
    /// </summary>
    public static async Task<RunningChild> StartAsync(string? heldFile, CancellationToken ct)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            Assert.Skip("Windows PowerShell 5.1 is not present on this host.");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        var start = new ProcessStartInfo(powershell, $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        if (heldFile is not null)
            start.Environment[HeldFileVariable] = heldFile;

        var child = new RunningChild(Process.Start(start)!);
        var line = await child.Process.StandardOutput.ReadLineAsync(ct);
        if (line != "ready")
        {
            child.Dispose();
            Assert.Fail($"the child did not start (read '{line}')");
        }
        return child;
    }

    /// <summary>
    /// True when the child echoes a line back. False once it has exited: its pipe is then broken or at its end, and
    /// either shows at once rather than after a timeout.
    /// </summary>
    public async Task<bool> AnswersAsync(CancellationToken ct)
    {
        try
        {
            await Process.StandardInput.WriteLineAsync("still-here".AsMemory(), ct);
            await Process.StandardInput.FlushAsync(ct);
        }
        catch (IOException)
        {
            return false;   // the pipe is broken: nothing is reading it
        }
        return await Process.StandardOutput.ReadLineAsync(ct) == "still-here";
    }

    public void Dispose()
    {
        // Closing its input ends the echo loop, and the child exits with it; killing covers a stuck one.
        try { Process.StandardInput.Close(); }
        catch (IOException) { /* the pipe is already broken */ }

        try { if (!Process.WaitForExit(TimeSpan.FromSeconds(10))) Process.Kill(); }
        catch (InvalidOperationException) { /* already gone */ }

        Process.Dispose();
    }
}
