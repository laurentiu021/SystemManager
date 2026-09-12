// SysManager · PowerShellRunner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Serilog;
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
public sealed class PowerShellRunner : IPowerShellRunner, IDisposable
{
    /// <summary>
    /// Serialises pipelines on this runner, because a reused runspace can only run one at a time.
    /// </summary>
    /// <remarks>
    /// A runspace per call needed no gate — each pipeline had its own. Reuse makes concurrent calls on one
    /// runner a conflict, so they queue. Low cost in practice: this type is registered Transient precisely
    /// so each consumer gets its own instance, and the consumers that make several calls already serialise
    /// them (<c>DnsService</c> has its own gate). The gate is here as the guarantee rather than as a
    /// dependency on that continuing to be true.
    /// </remarks>
    private readonly SemaphoreSlim _pipeline = new(1, 1);

    private readonly TimeSpan _idleRunspaceLifetime;
    private RunspaceResources? _cached;
    private Timer? _idleEviction;
    private bool _disposed;

    private readonly Func<Action, Task> _scheduleProcessStart;
    private readonly Func<System.Diagnostics.Process, CancellationToken, Task> _waitForProcessExit;
    private readonly Action<System.Diagnostics.Process> _terminateProcessTree;
    private readonly Func<Runspace, Task> _openRunspace;
    private readonly TimeSpan _openRunspaceTimeout;
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
        Func<(Runspace Runspace, IDisposable? ProcessInstance, IDisposable? Process)>? createRunspace = null,
        TimeSpan? openRunspaceTimeout = null,
        TimeSpan? idleRunspaceLifetime = null)
    {
        _idleRunspaceLifetime = idleRunspaceLifetime ?? IdleRunspaceLifetime;
        _scheduleProcessStart = scheduleProcessStart
            ?? throw new ArgumentNullException(nameof(scheduleProcessStart));
        _waitForProcessExit = waitForProcessExit
            ?? (static (process, cancellationToken) => process.WaitForExitAsync(cancellationToken));
        _terminateProcessTree = terminateProcessTree
            ?? (static process => process.Kill(entireProcessTree: true));
        _openRunspace = openRunspace
            ?? (static runspace => Task.Run(() => runspace.Open()));
        _openRunspaceTimeout = openRunspaceTimeout ?? DefaultOpenRunspaceTimeout;

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
        await _pipeline.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunOnLeasedRunspaceAsync(script, parameters, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArmIdleEviction();
            _pipeline.Release();
        }
    }

    private async Task<Collection<PSObject>> RunOnLeasedRunspaceAsync(
        string script,
        IDictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        // Open the runspace on a thread-pool thread — this can take
        // several hundred milliseconds and must not block the UI.
        var lease = await LeaseRunspaceAsync().ConfigureAwait(false);
        using var perCall = lease.Reusable ? null : lease.Resources;
        var runspace = lease.Resources.Runspace;

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

        // The registration above can fire while this instance is still NotStarted, and Stop() then throws
        // InvalidOperationException, which is swallowed — so nothing has asked the pipeline to stop, and
        // BeginInvoke goes on to run the script in full. The window is real rather than theoretical: leasing
        // and opening a runspace takes a few hundred milliseconds, which is the same order as the delay any
        // caller puts before cancelling.
        //
        // Measured on CI: a cancel at 390 ms against a script that loops on 50 ms sleeps, and the run
        // finished its whole 20 seconds reporting "not interrupted … completed on its own". A loop of that
        // shape IS interruptible — the blocking-call test below measured the same script cancelling in
        // 2.2 s — so the stop was not refused, it was lost.
        //
        // Re-asserting here closes the window, because BeginInvoke has returned by this point and the
        // instance will accept a Stop. Idempotent either way: a second Stop on an already-stopping pipeline
        // raises the same InvalidOperationException this swallows, and on a pipeline that is running it does
        // exactly what the registration intended to do the first time.
        if (cancellationToken.IsCancellationRequested)
        {
            try { ps.Stop(); } catch (InvalidOperationException) { }
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested
                                   && (IsPipelineStopped(ex) || IsRemotingTornDownByOurStop(ex)))
        {
            // Cancellation calls ps.Stop(), which makes EndInvoke throw. Surface the standard cancellation
            // signal so callers that catch OperationCanceledException treat a cancelled PowerShell run as
            // cancelled rather than as an error.
            //
            // Filtered by IsPipelineStopped rather than by catching PipelineStoppedException directly,
            // because WHICH type arrives depends on the transport — see that method. This arm used to name
            // the in-process type only, so on an elevated (out-of-process) runspace it never matched.
            //
            // The message distinguishes this from the post-await throw below, and that is diagnostic rather
            // than decorative: both raise OperationCanceledException, so without it a slow cancellation
            // cannot be told apart from a stop that never interrupted anything. #2206 was hard precisely
            // because the only evidence was an elapsed time.
            throw new OperationCanceledException(
                "The PowerShell pipeline was stopped by cancellation.", cancellationToken);
        }

        // A stopped pipeline does NOT always throw, and relying on the catch above alone reported a
        // cancelled run as a successful one (#2206).
        //
        // Two ways to get here with the token cancelled, both measured rather than reasoned about:
        // opening the runspace above takes a few hundred milliseconds, so a cancel that lands during the
        // lease runs this callback synchronously (Register does that when the token is already cancelled)
        // and ps.Stop() hits a NotStarted instance — BeginInvoke/EndInvoke then complete without throwing
        // and without running the script. Locally that is every time: the token fired at 318 ms, the call
        // returned at 334 ms, no exception, no output. And on CI the opposite end of the same hole — Stop
        // failing to interrupt a running pipeline, which then finishes normally 30 seconds later.
        //
        // Either way the caller was handed an empty result set and no indication that anything was
        // cancelled. Every consumer is written around OperationCanceledException — 31 view-models have a
        // cancel branch and the services deliberately let it propagate — so a silent empty return does not
        // merely lose the signal, it looks like a successful run that found nothing. Which for a query is
        // indistinguishable from a real answer.
        //
        // Deliberately the OPPOSITE choice from RunProcessAsync, which on the same race lets completion win
        // ("callers receive the real exit code instead of a false cancellation"). That is right there and
        // wrong here, and the difference is what the method returns: an exit code from a process that
        // finished is a true and complete answer worth preserving, whereas an empty collection from a script
        // that never ran is not an answer at all. There is nothing here to preserve by staying silent.
        //
        // Thrown explicitly rather than via ThrowIfCancellationRequested() so the message says WHICH of the
        // two cancellation paths ran. Reaching here means the pipeline was never interrupted — it either
        // never started or ran to its natural end — which is a different fact from the catch arm above and
        // the one #2206 needed five CI failures to establish.
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The PowerShell pipeline was not interrupted by cancellation; it completed on its own.",
                cancellationToken);
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

    /// <summary>
    /// The single environment variable PowerShell 7 consults before sending telemetry.
    /// </summary>
    internal const string TelemetryOptOutVariable = "POWERSHELL_TELEMETRY_OPTOUT";

    /// <summary>
    /// Opts the hosted PowerShell out of telemetry before any runspace exists.
    /// </summary>
    /// <remarks>
    /// The in-process runspace is the PowerShell 7 SDK, and <c>System.Management.Automation</c> pulls in
    /// <c>Microsoft.ApplicationInsights</c> for one reason: its telemetry subsystem. That DLL is not
    /// theoretical here — it resolves in the dependency graph and ships inside the self-contained
    /// single-file .exe. PowerShell gates the whole subsystem on this one variable and nothing else.
    /// <para>SysManager's standing promise, repeated on every release page, is that it transfers nothing
    /// anywhere unless the user asks. Hosting a runspace that could report module loads to a third party
    /// would contradict the only claim the product makes about itself, so the opt-out is set here rather
    /// than trusted to a default.</para>
    /// <para>A static constructor, not <c>App.OnStartup</c>: this runs before the first instance method
    /// touches an SMA type, and it covers every host — the app, the test suite, and any future entry
    /// point — instead of only the one that remembers to call it.</para>
    /// <para><see cref="EnvironmentVariableTarget.Process"/> deliberately. This changes nothing the user
    /// can see or keep; the machine and user scopes are what <c>EnvironmentVariableService</c> edits on
    /// their behalf, and writing there would be a side effect nobody asked for. The elevated path starts
    /// Windows PowerShell 5.1 as a child process, which inherits this, so one assignment covers both.</para>
    /// </remarks>
    static PowerShellRunner() => OptOutOfPowerShellTelemetry();

    /// <summary>
    /// Sets the opt-out. Idempotent, and called from both the static constructor and
    /// <see cref="CreateRunspace"/>.
    /// </summary>
    /// <remarks>
    /// Belt and braces on purpose. The static constructor alone would be enough in practice, but a
    /// no-telemetry guarantee should not rest on when the runtime decides to initialise a type — and the
    /// first version of this proved the hazard is real: exposing the name as a <c>const</c> meant reading it
    /// inlined the literal and initialised nothing, so the accompanying test was red for a genuine reason.
    /// Calling it again at the one place a runspace is born makes the ordering unconditional.
    /// </remarks>
    internal static void OptOutOfPowerShellTelemetry()
        => Environment.SetEnvironmentVariable(
            TelemetryOptOutVariable, "1", EnvironmentVariableTarget.Process);

    private (Runspace Runspace, IDisposable? ProcessInstance, IDisposable? Process) CreateRunspace()
    {
        // Unconditional, immediately before the only runspace creation in the app, so the opt-out cannot
        // depend on type-initialisation order. Idempotent, so calling it per runspace costs nothing.
        OptOutOfPowerShellTelemetry();

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
            // The runspace was never created, so the child started above has nothing to serve. Stop it rather
            // than only dropping the handle — an orphan here keeps its pipes open exactly like a leaked one.
            if (runspace is null)
                DisposeRunspaceResources(null, processInstance, process, ReleaseProcess);
        }
    }

    private RunspaceResources CreateRunspaceResources()
    {
        try
        {
            var (runspace, processInstance, process) = _createRunspace();
            return new RunspaceResources(runspace, processInstance, process, ReleaseProcess);
        }
        catch (Exception ex) when (_isElevated && IsPowerShellHostUnavailable(ex))
        {
            throw CreatePowerShellHostUnavailableException(ex);
        }
    }

    /// <summary>
    /// How long a reusable runspace may sit unused before it and its child process are released.
    /// </summary>
    /// <remarks>
    /// The benefit of reuse is in BURSTS, not over a session: <c>DnsService</c> makes six elevated calls,
    /// <c>EdgeOneDriveService</c> four, three services three each, and those arrive together. Twenty seconds
    /// covers a burst with room for a slow one in the middle.
    /// <para>Keeping it for the session instead would be the obvious reading of "one runspace per session"
    /// and it is the wrong one. Nothing disposes most of these runners — nine are constructed directly in
    /// <c>MainWindowViewModel</c>'s designer graph and live as long as the window — so a session-long cache
    /// would mean a dozen <c>powershell.exe</c> processes resident for the whole run, tens of MB each, in an
    /// app whose entire pitch is making a PC feel faster. Eviction is what makes reuse a latency win rather
    /// than a memory trade.</para>
    /// </remarks>
    internal static readonly TimeSpan IdleRunspaceLifetime = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A runspace ready to run a pipeline, and whether it belongs to the cache or to this call alone.
    /// </summary>
    private readonly record struct RunspaceLease(RunspaceResources Resources, bool Reusable);

    /// <summary>
    /// Returns an open runspace: the cached one when it is still usable, otherwise a fresh one.
    /// </summary>
    /// <remarks>
    /// <para><b>Elevated only.</b> The in-process runspace the unelevated path uses starts no child process
    /// and opens in a fraction of the time, so caching it would add a lifetime to reason about and buy
    /// almost nothing. The out-of-process branch is the one that spawns <c>powershell.exe</c> 5.1 and
    /// completes a remoting handshake, and it is the only branch #2149 is about.</para>
    /// <para><b>State is re-checked every time, not assumed.</b> A cached runspace can be broken by things
    /// outside this class — the child killed by a user or by cleanup, the remoting channel dropped — and a
    /// runspace that is not <c>Opened</c> cannot run a pipeline. Anything other than <c>Opened</c> means
    /// discard and rebuild, which also covers the state this code cannot enumerate in advance.</para>
    /// <para><b>A failed open leaves nothing cached.</b> The fresh resources are disposed and the exception
    /// propagates, so the next call starts clean rather than retrying against a half-opened runspace.</para>
    /// </remarks>
    /// <summary>
    /// True when <paramref name="ex"/> means "the pipeline was stopped", whichever transport reported it.
    /// </summary>
    /// <remarks>
    /// The type depends on WHERE the pipeline ran, which is why naming one of them was not enough (#2206):
    /// <list type="bullet">
    /// <item>unelevated, in-process runspace → <see cref="PipelineStoppedException"/> directly;</item>
    /// <item>elevated, out-of-process Windows PowerShell 5.1 over remoting →
    /// <see cref="RemoteException"/> whose <c>SerializedRemoteException</c> is the stopped-pipeline error,
    /// because the failure happened in the child and was serialized across the transport.</item>
    /// </list>
    /// <para>The arm above named the in-process type only, so on an ELEVATED runspace it never matched and
    /// cancellation escaped as a raw PowerShell error. That is invisible on a developer machine, which runs
    /// unelevated and takes the first branch, and it is what CI kept hitting: the failure reported
    /// <c>RemoteException (The pipeline has been stopped.)</c> — the right event, the wrong type, no
    /// translation. Nine words of a CI log that a local run could not have produced.</para>
    /// <para><b>The remote arm checks the TYPE NAME, not the type.</b> Remoting does not hand back the
    /// original exception object — it rehydrates a <c>PSObject</c> whose <c>TypeNames</c> read
    /// <c>Deserialized.System.Management.Automation.PipelineStoppedException</c>, so an <c>is</c> test
    /// against the real type is always false. Suffix-matching the type name is what actually holds, and it
    /// is still locale-independent, unlike matching the message: a non-English Windows would fall out of a
    /// string comparison on "The pipeline has been stopped." without anything saying so.</para>
    /// <para><c>internal</c> so the shapes can be asserted directly, rather than needing an elevated host
    /// to produce a real remote failure.</para>
    /// </remarks>
    internal static bool IsPipelineStopped(Exception ex) =>
        ex is PipelineStoppedException
        || ex.InnerException is PipelineStoppedException
        || (ex as RemoteException)?.SerializedRemoteException?.TypeNames
               .Any(name => name.EndsWith("PipelineStoppedException", StringComparison.Ordinal)) == true;

    /// <summary>
    /// The out-of-process transport reporting the stop WE asked for: a remoting data-structure fault raised
    /// while cancellation is already requested.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="IsPipelineStopped"/>, which answers "does this exception mean
    /// the pipeline was stopped" and must keep answering it honestly —
    /// <c>PSRemotingDataStructureException</c> is a general remoting protocol fault, so folding it in there
    /// would make a genuine transport breakdown read as a stop, and that method's negative tests exist to
    /// prevent exactly that.
    /// <para><b>What makes the weaker inference safe is the caller, not the type.</b> The one call site is
    /// guarded by <c>cancellationToken.IsCancellationRequested</c>, so this is only ever consulted after we
    /// have called <c>Stop()</c> ourselves. With no cancellation requested the exception propagates as
    /// itself, which is what a real remoting failure must do.</para>
    /// <para>Found by CI, and only by CI: the in-process runspace this workstation uses raises
    /// <c>PipelineStoppedException</c>, which the method above already matches. Closing the lost-stop window
    /// in #2286 meant the stop started landing on the elevated out-of-process transport too, where
    /// <c>EndInvoke</c> instead threw <c>PSRemotingDataStructureException("The remote pipeline has been
    /// stopped.")</c> — so a cancellation that now WORKED surfaced as a raw remoting error rather than as
    /// <c>OperationCanceledException</c>. Matched by TYPE and not by that message, which is a sentence from
    /// PowerShell and not a contract.</para>
    /// </remarks>
    internal static bool IsRemotingTornDownByOurStop(Exception ex) =>
        ex is System.Management.Automation.Remoting.PSRemotingDataStructureException
        || ex.InnerException is System.Management.Automation.Remoting.PSRemotingDataStructureException;

    private async Task<RunspaceLease> LeaseRunspaceAsync()
    {
        if (!_isElevated)
        {
            var single = CreateRunspaceResources();
            try
            {
                await OpenRunspaceAsync(single.Runspace).ConfigureAwait(false);
            }
            catch
            {
                single.Dispose();
                throw;
            }

            return new RunspaceLease(single, Reusable: false);
        }

        if (_cached is { } cached)
        {
            if (cached.Runspace.RunspaceStateInfo.State == RunspaceState.Opened)
                return new RunspaceLease(cached, Reusable: true);

            Log.Debug("PowerShell: cached runspace is {State}; rebuilding it",
                      cached.Runspace.RunspaceStateInfo.State);
            _cached = null;
            cached.Dispose();
        }

        var fresh = CreateRunspaceResources();
        try
        {
            await OpenRunspaceAsync(fresh.Runspace).ConfigureAwait(false);
        }
        catch
        {
            fresh.Dispose();
            throw;
        }

        _cached = fresh;
        return new RunspaceLease(fresh, Reusable: true);
    }

    /// <summary>
    /// Restarts the idle countdown after a run, creating the timer on first use.
    /// </summary>
    /// <remarks>
    /// Called from the <c>finally</c> of every run, inside the pipeline gate, so it cannot race the lease.
    /// Nothing is armed when there is no cache to evict — the unelevated path never creates a timer at all.
    /// </remarks>
    private void ArmIdleEviction()
    {
        if (_cached is null || _disposed) return;

        _idleEviction ??= new Timer(_ => EvictIdleRunspace(), null, Timeout.Infinite, Timeout.Infinite);
        _idleEviction.Change(_idleRunspaceLifetime, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Releases the cached runspace and its child process once it has been idle.
    /// </summary>
    /// <remarks>
    /// Takes the pipeline gate with a zero wait rather than blocking on it. A run that started between the
    /// timer firing and this callback holds the gate, and tearing its runspace down underneath it would turn
    /// a working call into a broken one; re-arming instead costs one more idle period and cannot.
    /// </remarks>
    private void EvictIdleRunspace()
    {
        if (!_pipeline.Wait(0)) { ArmIdleEviction(); return; }

        try
        {
            var idle = _cached;
            _cached = null;
            idle?.Dispose();
        }
        finally
        {
            _pipeline.Release();
        }
    }

    /// <summary>
    /// Releases the cached runspace, its child process and the idle timer.
    /// </summary>
    /// <remarks>
    /// Deterministic release for the consumers that are disposed. It is NOT the only release path and must
    /// not be: nine runners are constructed directly in <c>MainWindowViewModel</c>'s designer graph and
    /// nothing ever disposes them, which is exactly why eviction is on a timer rather than on Dispose.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _idleEviction?.Dispose();

        var cached = _cached;
        _cached = null;
        cached?.Dispose();

        _pipeline.Dispose();
    }

    /// <summary>
    /// How long to wait for a runspace to become ready before giving up on it.
    /// </summary>
    /// <remarks>
    /// Deliberately generous rather than tight. Opening the elevated runspace starts a <c>powershell.exe</c>
    /// 5.1 child and completes a handshake with it, and a cold start on a slow or busy machine is legitimately
    /// slow — a timeout that fired on that would turn a working feature into a broken one, which is worse than
    /// the defect it fixes. What it has to beat is not "slow", it is "never".
    /// <para>A hang dump from the integration job showed <b>275 threads</b> parked in
    /// <c>RemoteRunspace.Open</c> on a host saturated with leaked runspaces (#2149), and <c>Open()</c> takes no
    /// timeout of its own. Sixty seconds turns a permanently busy tab into an error the existing
    /// unavailable-host handling already knows how to show.</para>
    /// </remarks>
    internal static readonly TimeSpan DefaultOpenRunspaceTimeout = TimeSpan.FromSeconds(60);

    private async Task OpenRunspaceAsync(Runspace runspace)
    {
        try
        {
            // WaitAsync rather than a token passed inward: Open() offers neither cancellation nor a timeout
            // of its own, so there is nothing to hand it. This bounds the WAIT, not the work — the open may
            // still be in flight when we give up, and the caller's `using` disposes the runspace either way.
            await _openRunspace(runspace).WaitAsync(_openRunspaceTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // NOT gated on _isElevated, unlike the mapping below. The out-of-process path is elevated-only, so
            // that is where a hang has actually been observed — but an unbounded wait is the wrong behaviour
            // either way, and gating this would leave the in-process path with no answer at all.
            //
            // Thrown as RuntimeException like its neighbour, because callers already map that onto their
            // established unavailable/failed states. A timeout that surfaced as a novel exception type would
            // reach them as an unhandled fault instead of as "PowerShell is not available".
            throw new RuntimeException(
                $"Windows PowerShell did not become ready within {_openRunspaceTimeout.TotalSeconds:0} "
                + "seconds and the request was abandoned.",
                ex);
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

    /// <summary>
    /// Tears down a runspace and the child process behind it, in dependency order.
    /// </summary>
    /// <param name="releaseProcess">
    /// How to release the child. Defaults to disposing the handle, which is all this used to do; the elevated
    /// path passes <see cref="ReleaseProcess"/>, which also stops a child that is still running.
    /// </param>
    /// <remarks>
    /// Disposing a <see cref="System.Diagnostics.Process"/> releases the HANDLE and does not stop the process.
    /// That is the whole of the leak in #2149: when <c>powershell.exe</c> outlived the runspace, its stdout and
    /// stderr pipes stayed open, and the remoting transport's reader threads stayed blocked in <c>ReadFile</c>
    /// forever. A hang dump from the integration job showed 1,112 such threads — about four per runspace, so
    /// roughly 276 children still alive in one process.
    /// <para>Closing the runspace before disposing it is deliberately NOT done here. It is the obvious next
    /// idea and it carries a real risk: 275 of those runspaces were parked inside <c>Open()</c>, and
    /// <c>Close()</c> on a runspace that never finished opening can block on the same handshake. Stopping the
    /// child is what actually frees the threads, and it cannot block on the runspace's state.</para>
    /// </remarks>
    internal static void DisposeRunspaceResources(
        IDisposable? runspace,
        IDisposable? processInstance,
        IDisposable? process,
        Action<IDisposable?>? releaseProcess = null)
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
                (releaseProcess ?? (static p => p?.Dispose()))(process);
            }
        }
    }

    /// <summary>
    /// Stops the child process behind an out-of-process runspace if it is still running, then disposes the
    /// handle. Never throws: teardown runs in a <c>finally</c>.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="TryTerminateForCancellationAsync"/>'s handling of the same races — the process can
    /// exit between the <c>HasExited</c> check and the kill, and a tree termination can fail outright — because
    /// they are the same races, not because the shape is tidy. The difference is that this one has nothing to
    /// report to: a failure to stop the child leaks what was already leaking, and throwing out of a
    /// <c>finally</c> would replace that with losing the runspace's disposal too.
    /// </remarks>
    private void ReleaseProcess(IDisposable? process)
    {
        try
        {
            if (process is System.Diagnostics.Process child && !child.HasExited)
                _terminateProcessTree(child);
        }
        catch (InvalidOperationException)
        {
            // Already gone, or the handle no longer refers to a live process. Either way there is nothing
            // left to stop.
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or
            AggregateException or
            NotSupportedException)
        {
            Log.Debug(ex, "PowerShell: could not stop the runspace's child process during teardown");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private sealed class RunspaceResources : IDisposable
    {
        private readonly IDisposable? _processInstance;
        private readonly IDisposable? _process;
        private readonly Action<IDisposable?> _releaseProcess;

        public RunspaceResources(
            Runspace runspace,
            IDisposable? processInstance,
            IDisposable? process,
            Action<IDisposable?> releaseProcess)
        {
            Runspace = runspace ?? throw new ArgumentNullException(nameof(runspace));
            _processInstance = processInstance;
            _process = process;
            _releaseProcess = releaseProcess ?? throw new ArgumentNullException(nameof(releaseProcess));
        }

        public Runspace Runspace { get; }

        public void Dispose()
            => DisposeRunspaceResources(Runspace, _processInstance, _process, _releaseProcess);
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
