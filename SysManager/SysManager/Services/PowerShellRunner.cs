// SysManager · PowerShellRunner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Runs PowerShell scripts with live streaming of all output streams. Normal sessions
/// use an in-process runspace; elevated sessions use an isolated Windows PowerShell 5.1
/// child so per-user module paths never enter an administrator runspace.
///
/// <para><b>Security note (SEC-005 / SEC-M8):</b> ExecutionPolicy is set to Bypass because
/// SysManager only executes its own static scripts — never user-supplied or downloaded
/// scripts. Elevated child processes also receive a machine-owned-only module path.</para>
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
    private readonly Func<Runspace, Task> _openRunspace;
    private readonly Func<(Runspace Runspace, IDisposable? ProcessInstance, IDisposable? Process)> _createRunspace;
    private readonly bool _isElevated;
    private readonly string _trustedPowerShellModulePath;

    public PowerShellRunner()
        : this(action => Task.Run(action))
    {
    }

    internal PowerShellRunner(
        Func<Action, Task> scheduleProcessStart,
        Func<System.Diagnostics.Process, CancellationToken, Task>? waitForProcessExit = null,
        Action<System.Diagnostics.Process>? terminateProcessTree = null,
        Func<bool>? isElevated = null,
        string? trustedPowerShellModulePath = null,
        Func<Runspace, Task>? openRunspace = null,
        Func<(Runspace Runspace, IDisposable? ProcessInstance, IDisposable? Process)>? createRunspace = null)
    {
        _scheduleProcessStart = scheduleProcessStart
            ?? throw new ArgumentNullException(nameof(scheduleProcessStart));
        _waitForProcessExit = waitForProcessExit
            ?? (static (process, cancellationToken) => process.WaitForExitAsync(cancellationToken));
        _terminateProcessTree = terminateProcessTree
            ?? (static process => process.Kill(entireProcessTree: true));
        _openRunspace = openRunspace
            ?? (static runspace => Task.Run(() => runspace.Open()));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        _isElevated = (isElevated ?? Helpers.AdminHelper.IsElevated)();
        var configuredModulePath = trustedPowerShellModulePath
            ?? BuildTrustedPowerShellModulePath(
                programFiles,
                Environment.SystemDirectory);
        _trustedPowerShellModulePath = EnsureDistinctFromMachinePowerShellModulePath(
            configuredModulePath,
            Environment.GetEnvironmentVariable(
                "PSModulePath",
                EnvironmentVariableTarget.Machine),
            programFiles);
        _createRunspace = createRunspace ?? CreateRunspace;
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
    /// Execute a script and return the collected PSObject results. Elevated execution
    /// is isolated in a child process with a sanitized module path.
    /// All streams are forwarded via <see cref="LineReceived"/> for live UI display.
    /// </summary>
    public async Task<Collection<PSObject>> RunAsync(
        string script,
        IDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        using var resources = CreateRunspaceResources();
        var runspace = resources.Runspace;
        // Open the runspace on a thread-pool thread — this can take
        // several hundred milliseconds and must not block the UI.
        await OpenRunspaceAsync(runspace).ConfigureAwait(false);

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
            // treat a cancelled PowerShell run as cancelled rather than as an error.
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
        var modulePathInitialization = _isElevated
            ? BuildPowerShellModulePathAssignment(_trustedPowerShellModulePath)
            : string.Empty;
        var wrapped =
            modulePathInitialization + "$ProgressPreference='SilentlyContinue';" +
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
        ApplyTrustedPowerShellModulePath(
            psi,
            _isElevated,
            _trustedPowerShellModulePath);

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

    internal static string BuildTrustedPowerShellModulePath(
        string programFiles,
        string systemDirectory)
    {
        if (string.IsNullOrWhiteSpace(programFiles) ||
            !System.IO.Path.IsPathFullyQualified(programFiles))
        {
            throw new ArgumentException(
                "Program Files must be a fully qualified path.",
                nameof(programFiles));
        }

        if (string.IsNullOrWhiteSpace(systemDirectory) ||
            !System.IO.Path.IsPathFullyQualified(systemDirectory))
        {
            throw new ArgumentException(
                "The system directory must be a fully qualified path.",
                nameof(systemDirectory));
        }

        var trustedRoots = new[]
        {
            System.IO.Path.Combine(programFiles, "WindowsPowerShell", "Modules"),
            System.IO.Path.Combine(
                systemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "Modules"),
            System.IO.Path.Combine(programFiles, "PowerShell", "Modules")
        };

        return string.Join(
            System.IO.Path.PathSeparator,
            trustedRoots
                .Select(System.IO.Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    internal static string EnsureDistinctFromMachinePowerShellModulePath(
        string trustedModulePath,
        string? machineModulePath,
        string programFiles)
    {
        if (string.IsNullOrWhiteSpace(trustedModulePath))
        {
            throw new ArgumentException(
                "The trusted PowerShell module path cannot be empty.",
                nameof(trustedModulePath));
        }

        if (!string.Equals(
                trustedModulePath,
                machineModulePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return trustedModulePath;
        }

        if (string.IsNullOrWhiteSpace(programFiles) ||
            !System.IO.Path.IsPathFullyQualified(programFiles))
        {
            throw new ArgumentException(
                "Program Files must be a fully qualified path.",
                nameof(programFiles));
        }

        var guardRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(
                programFiles,
                "SysManager",
                "PowerShellModules"));
        return string.Join(
            System.IO.Path.PathSeparator,
            trustedModulePath,
            guardRoot);
    }

    internal static string BuildPowerShellModulePathAssignment(string trustedModulePath)
    {
        if (string.IsNullOrWhiteSpace(trustedModulePath))
        {
            throw new ArgumentException(
                "The trusted PowerShell module path cannot be empty.",
                nameof(trustedModulePath));
        }

        var escapedPath = trustedModulePath.Replace("'", "''", StringComparison.Ordinal);
        return $"$env:PSModulePath='{escapedPath}';";
    }

    private (Runspace Runspace, IDisposable? ProcessInstance, IDisposable? Process) CreateRunspace()
    {
        if (!_isElevated)
        {
            var initialSessionState = InitialSessionState.CreateDefault2();
            initialSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            return (RunspaceFactory.CreateRunspace(initialSessionState), null, null);
        }

        var processInstance = new PowerShellProcessInstance(
            new Version(5, 1),
            credential: null,
            initializationScript: ScriptBlock.Create(
                BuildPowerShellModulePathAssignment(_trustedPowerShellModulePath)),
            useWow64: false);
        var process = processInstance.Process;
        Runspace? runspace = null;
        try
        {
            ApplyTrustedPowerShellModulePath(
                processInstance.Process.StartInfo,
                isElevated: true,
                _trustedPowerShellModulePath);

            runspace = RunspaceFactory.CreateOutOfProcessRunspace(
                TypeTable.LoadDefaultTypeFiles(),
                processInstance);
            return (runspace, processInstance, process);
        }
        finally
        {
            if (runspace is null)
                DisposeRunspaceResources(null, processInstance, process);
        }
    }

    private RunspaceResources CreateRunspaceResources()
    {
        try
        {
            var (runspace, processInstance, process) = _createRunspace();
            return new RunspaceResources(runspace, processInstance, process);
        }
        catch (Exception ex) when (_isElevated && IsPowerShellHostUnavailable(ex))
        {
            throw CreatePowerShellHostUnavailableException(ex);
        }
    }

    private async Task OpenRunspaceAsync(Runspace runspace)
    {
        try
        {
            await _openRunspace(runspace).ConfigureAwait(false);
        }
        catch (Exception ex) when (_isElevated && IsPowerShellHostUnavailable(ex))
        {
            // Keep the isolation boundary fail-closed. Callers already map RuntimeException
            // to their established unavailable/failed states; an in-process fallback here
            // would reintroduce per-user module discovery under the administrator token.
            throw CreatePowerShellHostUnavailableException(ex);
        }
    }

    private static bool IsPowerShellHostUnavailable(Exception exception)
        => exception is System.Management.Automation.Remoting.PSRemotingTransportException or
            PSInvalidOperationException or
            System.ComponentModel.Win32Exception or
            System.IO.FileNotFoundException or
            UnauthorizedAccessException ||
            exception is TypeInitializationException { InnerException: { } innerException } &&
            (IsPowerShellHostUnavailable(innerException) ||
             innerException is ArgumentException or
                 System.Security.SecurityException or
                 System.IO.IOException);

    private static RuntimeException CreatePowerShellHostUnavailableException(Exception innerException)
        => new(
            "Windows PowerShell 5.1 is unavailable or blocked by system policy.",
            innerException);

    internal static void DisposeRunspaceResources(
        IDisposable? runspace,
        IDisposable? processInstance,
        IDisposable? process)
    {
        try
        {
            runspace?.Dispose();
        }
        finally
        {
            try
            {
                processInstance?.Dispose();
            }
            finally
            {
                process?.Dispose();
            }
        }
    }

    private sealed class RunspaceResources : IDisposable
    {
        private readonly IDisposable? _processInstance;
        private readonly IDisposable? _process;

        public RunspaceResources(
            Runspace runspace,
            IDisposable? processInstance,
            IDisposable? process)
        {
            Runspace = runspace ?? throw new ArgumentNullException(nameof(runspace));
            _processInstance = processInstance;
            _process = process;
        }

        public Runspace Runspace { get; }

        public void Dispose()
            => DisposeRunspaceResources(Runspace, _processInstance, _process);
    }

    internal static void ApplyTrustedPowerShellModulePath(
        System.Diagnostics.ProcessStartInfo startInfo,
        bool isElevated,
        string trustedModulePath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!isElevated || !IsPowerShellExecutable(startInfo.FileName))
            return;

        startInfo.Environment["PSModulePath"] = trustedModulePath;
    }

    private static bool IsPowerShellExecutable(string fileName)
    {
        var leafName = System.IO.Path.GetFileName(fileName);
        return leafName.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
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
