// SysManager · PowerShellRunner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Runs PowerShell scripts in-process with live streaming of all output streams.
/// Uses System.Management.Automation so we don't spawn external pwsh.exe processes.
///
/// <para><b>Security note (SEC-005 / SEC-M8):</b> ExecutionPolicy is set to Bypass for
/// in-process runspaces because SysManager only executes its own static scripts — never
/// user-supplied or downloaded scripts. RunScriptViaPwshAsync also uses -ExecutionPolicy
/// Bypass for the same reason.</para>
///
/// <para><b>SECURITY CONTRACT:</b> Callers MUST only pass hard-coded script strings to
/// RunAsync and RunScriptViaPwshAsync. User input MUST NEVER be interpolated into scripts.
/// Violation of this contract creates a code injection vulnerability. The Bypass policy
/// is safe ONLY because the script content is fully controlled by SysManager's source code.</para>
/// </summary>
public sealed class PowerShellRunner : IPowerShellRunner
{
    private readonly Func<Action, Task> _scheduleProcessStart;
    private readonly Func<System.Diagnostics.Process, CancellationToken, Task> _waitForProcessExit;
    private readonly Action<System.Diagnostics.Process> _terminateProcessTree;

    public PowerShellRunner()
        : this(action => Task.Run(action))
    {
    }

    internal PowerShellRunner(
        Func<Action, Task> scheduleProcessStart,
        Func<System.Diagnostics.Process, CancellationToken, Task>? waitForProcessExit = null,
        Action<System.Diagnostics.Process>? terminateProcessTree = null)
    {
        _scheduleProcessStart = scheduleProcessStart
            ?? throw new ArgumentNullException(nameof(scheduleProcessStart));
        _waitForProcessExit = waitForProcessExit
            ?? (static (process, cancellationToken) => process.WaitForExitAsync(cancellationToken));
        _terminateProcessTree = terminateProcessTree
            ?? (static process => process.Kill(entireProcessTree: true));
    }

    /// <summary>
    /// Raised for each line of output from any stream (stdout, stderr, information,
    /// warning, error, verbose, debug, progress). Fires on a thread-pool thread —
    /// subscribers that update UI elements must marshal to the dispatcher.
    /// </summary>
    public event Action<PowerShellLine>? LineReceived;
    public event Action<int>? ProgressChanged; // 0-100

    /// <summary>
    /// OEM encoding for native Windows tools (chkdsk, sfc, DISM, ipconfig,
    /// netsh, powercfg, sc). Requires CodePagesEncodingProvider registered at startup.
    /// </summary>
    public static System.Text.Encoding OemEncoding { get; } =
        GetOemEncodingSafe();

    private static System.Text.Encoding GetOemEncodingSafe()
    {
        try
        {
            return System.Text.Encoding.GetEncoding(
                System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (NotSupportedException)
        {
            return System.Text.Encoding.UTF8;
        }
    }

    /// <summary>
    /// Execute a script and return the collected PSObject results.
    /// All streams are forwarded via <see cref="LineReceived"/> for live UI display.
    /// </summary>
    public async Task<Collection<PSObject>> RunAsync(
        string script,
        IDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var iss = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        using var runspace = RunspaceFactory.CreateRunspace(iss);
        // Open the runspace on a thread-pool thread — this can take
        // several hundred milliseconds and must not block the UI.
        await Task.Run(() => runspace.Open()).ConfigureAwait(false);

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(script);
        if (parameters is not null)
        {
            foreach (var kv in parameters)
                ps.AddParameter(kv.Key, kv.Value);
        }

        // Hook all streams
        ps.Streams.Information.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<InformationRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Info, rec.MessageData?.ToString() ?? string.Empty, DateTime.Now));
        };
        ps.Streams.Warning.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<WarningRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Warning, rec.Message, DateTime.Now));
        };
        ps.Streams.Error.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<ErrorRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Error, rec.ToString(), DateTime.Now));
        };
        ps.Streams.Verbose.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<VerboseRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Verbose, rec.Message, DateTime.Now));
        };
        ps.Streams.Debug.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<DebugRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Debug, rec.Message, DateTime.Now));
        };
        ps.Streams.Progress.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<ProgressRecord>)s!)[e.Index];
            if (rec.PercentComplete >= 0) ProgressChanged?.Invoke(rec.PercentComplete);
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Progress, $"{rec.Activity}: {rec.StatusDescription} ({rec.PercentComplete}%)", DateTime.Now));
        };

        using var output = new PSDataCollection<PSObject>();
        output.DataAdded += (s, e) =>
        {
            var obj = ((PSDataCollection<PSObject>)s!)[e.Index];
            if (obj?.BaseObject != null)
                LineReceived?.Invoke(new PowerShellLine(OutputKind.Output, obj.BaseObject.ToString() ?? string.Empty, DateTime.Now));
        };

        using var reg = cancellationToken.Register(() => { try { ps.Stop(); } catch (InvalidOperationException) { } });

        var task = Task.Factory.FromAsync(
            ps.BeginInvoke<PSObject, PSObject>(null, output),
            ar => ps.EndInvoke(ar));

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation calls ps.Stop(), which makes EndInvoke throw PipelineStoppedException.
            // Surface the standard cancellation signal so callers that catch OperationCanceledException
            // treat a cancelled in-process run as cancelled rather than as an error.
            throw new OperationCanceledException(cancellationToken);
        }

        return new Collection<PSObject>(output.ToList());
    }

    /// <summary>
    /// Run a PowerShell script via an external powershell.exe (Windows PS 5.1).
    /// This gives full access to built-in modules (Management, Utility, PSWindowsUpdate, etc.)
    /// without bundling them with our app. All output is streamed live.
    /// Suppresses progress/CLIXML noise by default.
    /// </summary>
    public async Task<int> RunScriptViaPwshAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        // Prefix to silence progress (which gets serialized as CLIXML in stderr
        // when pwsh runs under a non-PS host) and set UTF-8 for clean text.
        var wrapped =
            "$ProgressPreference='SilentlyContinue';" +
            "$WarningPreference='Continue';" +
            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
            script;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrapped));
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}";
        return await RunProcessAsync("powershell.exe", args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Convenience for running an external process (winget etc.) with live line streaming.
    /// </summary>
    public async Task<int> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default,
        System.Text.Encoding? outputEncoding = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Always launch from a neutral system directory so the spawned
        // process never inherits a "locked" CWD (e.g. a user's Downloads
        // folder on another drive, which causes "Access is denied" when
        // running chkdsk.exe even under elevation).
        var workingDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(workingDir) || !System.IO.Directory.Exists(workingDir))
            workingDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Default to UTF-8 for most tools. System tools like sfc.exe, DISM.exe,
        // and chkdsk.exe write in the OEM code page — callers should pass
        // Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).
        var enc = outputEncoding ?? System.Text.Encoding.UTF8;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            // Pin bare Windows tool names to their full System32 path so an attacker-planted
            // executable in the (possibly user-writable) app directory can't be run elevated.
            FileName = SysManager.Helpers.SystemPaths.ResolveSystemTool(fileName),
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };

        using var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && !IsClixmlNoise(e.Data))
                LineReceived?.Invoke(PowerShellLine.Output(e.Data));
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && !IsClixmlNoise(e.Data))
                LineReceived?.Invoke(PowerShellLine.Err(e.Data));
        };

        // Start on a worker thread. The second token check closes the queueing race:
        // cancellation before this delegate runs cannot start the executable.
        await _scheduleProcessStart(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }).ConfigureAwait(false);

        try
        {
            await _waitForProcessExit(proc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (await TryTerminateForCancellationAsync(
                    proc,
                    "Cancellation was requested, but the process may still be running.")
                .ConfigureAwait(false))
            {
                throw;
            }

            // The process completed before cancellation could terminate it. Completion
            // wins so callers receive the real exit code instead of a false cancellation.
        }
        // WaitForExitAsync returns when the process exits, but the asynchronous
        // BeginOutputReadLine/BeginErrorReadLine pumps may not have raised their final
        // lines yet — callers that snapshot captured output immediately can lose the
        // last lines. The parameterless WaitForExit() blocks until both readers have
        // flushed and reached end-of-stream. Cheap here (the process has already exited)
        // and only reached when not cancelled.
        await Task.Run(proc.WaitForExit, CancellationToken.None).ConfigureAwait(false);

        return proc.ExitCode;
    }

    /// <summary>
    /// Runs a validated executable through ShellExecute. Unlike
    /// <see cref="RunProcessAsync"/>, this lets an executable whose manifest requires
    /// administrator rights display its own UAC prompt instead of inheriting
    /// SysManager's token.
    /// </summary>
    public async Task<int> RunProcessWithShellAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var workingDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(workingDir) || !System.IO.Directory.Exists(workingDir))
            workingDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = SysManager.Helpers.SystemPaths.ResolveSystemTool(fileName),
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = workingDir
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        await _scheduleProcessStart(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }).ConfigureAwait(false);

        try
        {
            await _waitForProcessExit(process, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (await TryTerminateForCancellationAsync(
                    process,
                    "Cancellation was requested, but the uninstaller may still be running.")
                .ConfigureAwait(false))
            {
                throw;
            }

            // Shell execution finished before cancellation could take effect. Preserve
            // the completed uninstaller's exit code and terminal state.
        }

        return process.ExitCode;
    }

    private async Task<bool> TryTerminateForCancellationAsync(
        System.Diagnostics.Process process,
        string failureMessage)
    {
        if (process.HasExited)
            return false;

        try
        {
            _terminateProcessTree(process);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The process completed between the state check and the termination call.
            return false;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            AggregateException)
        {
            // A tree-termination failure can leave descendants running even when the
            // parent has exited. Never downgrade that partial failure to completion.
            throw new InvalidOperationException(failureMessage, ex);
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static bool IsClixmlNoise(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("#< CLIXML", StringComparison.Ordinal)
            || t.StartsWith("<Objs ", StringComparison.Ordinal)
            || t.StartsWith("<Obj ", StringComparison.Ordinal)
            || t.StartsWith("</Objs>", StringComparison.Ordinal);
    }
}
