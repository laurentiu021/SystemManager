// SysManager · WindowsPowerShellScript
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Text;

namespace SysManager.IntegrationTests;

/// <summary>
/// Runs a script in a real Windows PowerShell 5.1 — the engine an elevated SysManager uses — for tests that pin
/// PowerShell's own semantics, which a substituted runner cannot show.
/// </summary>
/// <remarks>
/// Callers shadow every cmdlet that would change the machine with a local function of the same name (a function
/// takes precedence over a cmdlet), and <see cref="StubsInEffect"/> makes the script refuse to run if one of
/// those shadows is not in effect — so a typo in a stub cannot let the real cmdlet through on an elevated runner.
/// </remarks>
internal static class WindowsPowerShellScript
{
    /// <summary>A line that stops the script before anything else runs unless each named command is a function.</summary>
    internal static string StubsInEffect(params string[] commands) =>
        $"foreach ($c in {string.Join(",", commands.Select(c => $"'{c}'"))}) {{ " +
        "if ((Get-Command $c).CommandType -ne 'Function') { throw \"stub for $c is not in effect\" } } ; ";

    /// <summary>
    /// Runs <paramref name="script"/> and returns its exit code, standard output and standard error. Bounded: a
    /// PowerShell that never exits is killed and fails the test rather than hanging it.
    /// </summary>
    /// <remarks>
    /// The two streams are kept apart on purpose. PowerShell's error display echoes the failing line of the script,
    /// and a one-line script carries its own success sentinel, so on a combined stream a script that FAILED would
    /// still appear to have printed it. Error text is also wrapped to a console width, so assert on a short token in
    /// <c>Error</c>, never on a sentence.
    /// </remarks>
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(string script)
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

        return (process.ExitCode, await stdout, await stderr);
    }
}
