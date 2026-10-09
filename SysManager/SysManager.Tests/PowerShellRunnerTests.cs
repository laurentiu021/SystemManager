// SysManager · PowerShellRunnerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PowerShellRunner"/> — pure-logic helper methods.
/// Actual process spawning is an integration test.
/// </summary>
public class PowerShellRunnerTests
{
    private static bool InvokeIsClixmlNoise(string line)
    {
        var m = typeof(PowerShellRunner).GetMethod("IsClixmlNoise", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)m.Invoke(null, new object[] { line })!;
    }

    [Theory]
    [InlineData("#< CLIXML")]
    [InlineData("  #< CLIXML")]
    [InlineData("<Objs Version=\"1.1\">")]
    [InlineData("  <Objs Version=\"1.1\">")]
    [InlineData("<Obj RefId=\"0\">")]
    [InlineData("</Objs>")]
    public void IsClixmlNoise_DetectsNoise(string line)
        => Assert.True(InvokeIsClixmlNoise(line));

    [Theory]
    [InlineData("Normal output line")]
    [InlineData("Error: something went wrong")]
    [InlineData("")]
    [InlineData("   some indented text")]
    [InlineData("CLIXML is mentioned but not at start")]
    public void IsClixmlNoise_PassesNormalLines(string line)
        => Assert.False(InvokeIsClixmlNoise(line));

    [Fact]
    public void Constructs()
    {
        var runner = new PowerShellRunner();
        Assert.NotNull(runner);
    }

    [Fact]
    public void BuildTrustedPowerShellModulePath_UsesOnlyMachineOwnedRoots()
    {
        var path = PowerShellRunner.BuildTrustedPowerShellModulePath(
            @"C:\Program Files",
            @"C:\Windows\System32");

        var roots = path.Split(Path.PathSeparator);
        Assert.Equal(
            new[]
            {
                @"C:\Program Files\WindowsPowerShell\Modules",
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
                @"C:\Program Files\PowerShell\Modules"
            },
            roots);
        Assert.DoesNotContain(roots, root =>
            root.Contains(@"C:\Users", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDistinctFromMachinePowerShellModulePath_WhenEqual_AppendsMachineOwnedGuard()
    {
        const string machinePath =
            @"C:\Program Files\WindowsPowerShell\Modules;" +
            @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules";

        var path = PowerShellRunner.EnsureDistinctFromMachinePowerShellModulePath(
            machinePath,
            machinePath,
            @"C:\Program Files");

        Assert.NotEqual(machinePath, path);
        Assert.Equal(
            new[]
            {
                @"C:\Program Files\WindowsPowerShell\Modules",
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
                @"C:\Program Files\SysManager\PowerShellModules"
            },
            path.Split(Path.PathSeparator));
        Assert.DoesNotContain(
            path.Split(Path.PathSeparator),
            root => root.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDistinctFromMachinePowerShellModulePath_WhenAlreadyDistinct_PreservesPath()
    {
        const string trustedPath = @"C:\Program Files\WindowsPowerShell\Modules";

        var path = PowerShellRunner.EnsureDistinctFromMachinePowerShellModulePath(
            trustedPath,
            @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
            @"C:\Program Files");

        Assert.Equal(trustedPath, path);
    }

    [Fact]
    public void BuildPowerShellModulePathAssignment_EscapesSingleQuotes()
    {
        var assignment = PowerShellRunner.BuildPowerShellModulePathAssignment(
            @"C:\Program Files\Owner's Modules");

        Assert.Equal(
            "$env:PSModulePath='C:\\Program Files\\Owner''s Modules';",
            assignment);
    }

    // The host-failure tests below run for both kinds of session. Since #2476 a standard user's runspace is the
    // same Windows PowerShell 5.1 child an administrator's is, so a blocked or missing host must reach the
    // caller as the same "unavailable" RuntimeException. Before it, the mapping was gated on elevation, and a
    // standard user got the raw PowerShell exception, which the services' RuntimeException catches let through.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_HostOpenFailure_IsNormalizedToRuntimeException(bool isElevated)
    {
        var runner = CreateRunnerWithOpenFailure(
            new PSInvalidOperationException("Windows PowerShell was blocked."),
            isElevated);

        var exception = await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        Assert.Contains(
            "Windows PowerShell 5.1 is unavailable",
            exception.Message,
            StringComparison.Ordinal);
        Assert.IsType<PSInvalidOperationException>(exception.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_TransportFailure_IsNormalizedToRuntimeException(bool isElevated)
    {
        var runner = CreateRunnerWithOpenFailure(
            new System.Management.Automation.Remoting.PSRemotingTransportException(
                "The child-process transport failed."),
            isElevated);

        var exception = await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        Assert.Contains(
            "Windows PowerShell 5.1 is unavailable",
            exception.Message,
            StringComparison.Ordinal);
        Assert.IsType<System.Management.Automation.Remoting.PSRemotingTransportException>(
            exception.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_HostCreationFailure_IsNormalizedToRuntimeException(bool isElevated)
    {
        var failure = new PSInvalidOperationException(
            "Windows PowerShell is unavailable.");
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: () => isElevated,
            trustedPowerShellModulePath:
                @"C:\Program Files\WindowsPowerShell\Modules;" +
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
            createRunspace: () => throw failure);

        var exception = await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        Assert.Contains(
            "Windows PowerShell 5.1 is unavailable",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Same(failure, exception.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_HostRegistrationFailure_IsNormalizedToRuntimeException(bool isElevated)
    {
        var registrationFailure = new System.Security.SecurityException(
            "Windows PowerShell registration is inaccessible.");
        var failure = new TypeInitializationException(
            typeof(PowerShellProcessInstance).FullName,
            registrationFailure);
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: () => isElevated,
            trustedPowerShellModulePath:
                @"C:\Program Files\WindowsPowerShell\Modules;" +
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
            createRunspace: () => throw failure);

        var exception = await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        var wrappedFailure = Assert.IsType<TypeInitializationException>(
            exception.InnerException);
        Assert.Same(failure, wrappedFailure);
        Assert.Same(registrationFailure, wrappedFailure.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_HostOpenFailure_DisposesInjectedProcessResources(bool isElevated)
    {
        var order = new List<string>();
        var processInstance = new RecordingDisposable("process instance", order);
        var process = new RecordingDisposable("process", order);
        var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: () => isElevated,
            trustedPowerShellModulePath:
                @"C:\Program Files\WindowsPowerShell\Modules;" +
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
            openRunspace: _ => Task.FromException(
                new PSInvalidOperationException("Windows PowerShell was blocked.")),
            createRunspace: () => (runspace, processInstance, process));

        await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        Assert.Equal(["process instance", "process"], order);
    }

    /// <summary>
    /// An open that never completes fails, rather than waiting forever.
    /// </summary>
    /// <remarks>
    /// <c>Runspace.Open()</c> takes neither a token nor a timeout, so nothing inside it can be asked to stop.
    /// Before this, a host that could not complete a handshake produced no exception and no return — a hang
    /// dump from the integration job showed 275 threads parked in <c>RemoteRunspace.Open</c> (#2149). The
    /// surrounding code is carefully fail-closed and maps transport, Win32 and file-not-found failures onto a
    /// "PowerShell host unavailable" state, but that mapping only fires on a THROW; a wait that never signals
    /// slips past all of it, and the user gets a tab that stays busy with Cancel gated on not-busy.
    /// <para>Deterministic, and no sleeping: the stub returns a task that is never completed, and the timeout
    /// is injected at 50 ms. Against the unbounded code this test does not fail — it never finishes, which is
    /// the defect stated as precisely as a test can state it.</para>
    /// <para><see cref="RuntimeException"/> on purpose, matching its neighbours: callers already map that type
    /// onto their unavailable/failed states, so a timeout arrives as "PowerShell is not available" rather than
    /// as an unhandled fault of a novel type.</para>
    /// </remarks>
    [Fact]
    public async Task RunAsync_OpenThatNeverCompletes_TimesOutInsteadOfHanging()
    {
        var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
        var neverCompletes = new TaskCompletionSource();
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            openRunspace: _ => neverCompletes.Task,
            createRunspace: () => (runspace, null, null),
            openRunspaceTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        Assert.Contains("did not become ready", ex.Message, StringComparison.Ordinal);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    /// <summary>
    /// The timeout applies whether or not the session is elevated.
    /// </summary>
    /// <remarks>
    /// Both kinds of session open the same out-of-process Windows PowerShell 5.1 child since #2476, so both can
    /// meet the hang the timeout exists for. Before that, only the elevated path used the child and the
    /// exception mapping beside the timeout was gated on elevation. The timeout never was, and this kept it that
    /// way; it now also keeps a future elevation check from creeping back in around it.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_OpenTimeout_AppliesRegardlessOfElevation(bool isElevated)
    {
        var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
        var neverCompletes = new TaskCompletionSource();
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: () => isElevated,
            trustedPowerShellModulePath:
                @"C:\Program Files\WindowsPowerShell\Modules;" +
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
            openRunspace: _ => neverCompletes.Task,
            createRunspace: () => (runspace, null, null),
            openRunspaceTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    /// <summary>The shipped timeout is generous enough not to fire on a slow cold start.</summary>
    /// <remarks>
    /// The value is the whole risk in this change: too tight and it breaks a working feature on a slow machine,
    /// which is worse than the hang it replaces. Asserted as a floor rather than an exact number so it can be
    /// raised without editing a test, and never lowered past the point where a cold <c>powershell.exe</c> start
    /// plus a handshake could plausibly still be in progress.
    /// </remarks>
    [Fact]
    public void DefaultOpenRunspaceTimeout_IsGenerousEnoughForAColdStart()
        => Assert.True(PowerShellRunner.DefaultOpenRunspaceTimeout >= TimeSpan.FromSeconds(30),
            $"the open timeout is {PowerShellRunner.DefaultOpenRunspaceTimeout.TotalSeconds:0}s; a cold "
            + "powershell.exe start and handshake on a slow machine can take longer than that, and a timeout "
            + "that fires on a working host is worse than the hang it replaces");

    /// <summary>
    /// The release action runs after the runspace and the process instance, not before them.
    /// </summary>
    /// <remarks>
    /// Ordering only. An earlier version of this test claimed to prove the WIRING — that the production path
    /// hands teardown <c>ReleaseProcess</c> rather than a bare <c>Dispose</c> — and it did not: it called
    /// <c>DisposeRunspaceResources</c> directly with its own lambda, so mutating what <c>RunspaceResources</c>
    /// passes left it green. It was asserting that the method honours its own argument, which is trivially
    /// true. Renamed to what it actually covers.
    /// <para>The wiring is proven end to end by
    /// <c>PowerShellRunnerTests.DisposingTheRunner_StopsTheChildItsRunspaceWasBuiltWith</c> in
    /// <c>SysManager.IntegrationTests</c>, which injects a real live child, runs through it, and asserts that
    /// disposing the runner killed it. That needs a real process, because the decision is
    /// <c>is Process &amp;&amp; !HasExited</c> and no double satisfies it.</para>
    /// </remarks>
    [Fact]
    public void DisposeRunspaceResources_ReleasesTheChildLast()
    {
        var order = new List<string>();
        var runspace = new RecordingDisposable("runspace", order);
        var processInstance = new RecordingDisposable("process instance", order);
        var process = new RecordingDisposable("process", order);

        PowerShellRunner.DisposeRunspaceResources(
            runspace,
            processInstance,
            process,
            releaseProcess: p =>
            {
                order.Add("released");
                p?.Dispose();
            });

        Assert.Equal(["runspace", "process instance", "released", "process"], order);
    }

    [Fact]
    public void DisposeRunspaceResources_DisposesInDependencyOrder()
    {
        var order = new List<string>();
        var runspace = new RecordingDisposable("runspace", order);
        var processInstance = new RecordingDisposable("process instance", order);
        var process = new RecordingDisposable("process", order);

        PowerShellRunner.DisposeRunspaceResources(
            runspace,
            processInstance,
            process);

        Assert.Equal(
            ["runspace", "process instance", "process"],
            order);
    }

    [Fact]
    public void DisposeRunspaceResources_RunspaceDisposeFails_StillDisposesProcessResources()
    {
        var order = new List<string>();
        var failure = new InvalidOperationException("Runspace disposal failed.");
        var runspace = new RecordingDisposable("runspace", order, failure);
        var processInstance = new RecordingDisposable("process instance", order);
        var process = new RecordingDisposable("process", order);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PowerShellRunner.DisposeRunspaceResources(
                runspace,
                processInstance,
                process));

        Assert.Same(failure, exception);
        Assert.Equal(
            ["runspace", "process instance", "process"],
            order);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefenderStatus_HostOpenFailure_ReturnsUnavailable(bool isElevated)
    {
        var runner = CreateRunnerWithOpenFailure(
            new PSInvalidOperationException("Windows PowerShell was blocked."),
            isElevated);
        var service = new DefenderService(runner);

        var status = await service.GetStatusAsync();

        Assert.False(status.Available);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DnsStatus_HostOpenFailure_ReturnsUnavailable(bool isElevated)
    {
        var runner = CreateRunnerWithOpenFailure(
            new PSInvalidOperationException("Windows PowerShell was blocked."),
            isElevated);
        using var service = new DnsService(runner);

        var status = await service.GetCurrentDnsAsync();

        Assert.Equal("Unavailable", status);
    }

    /// <summary>
    /// A standard user's runner builds the same out-of-process Windows PowerShell 5.1 runspace as an
    /// administrator's.
    /// </summary>
    /// <remarks>
    /// The root of #2476. A standard user's runner built an in-process runspace instead: PowerShell 7 hosted from
    /// the SDK package alone, which loads <c>Microsoft.PowerShell.Core</c> and nothing else. Every script that
    /// named a Utility, Management, Appx or Defender cmdlet therefore failed and returned nothing.
    /// <para>No process is started. The open is stubbed to fail, so the runspace is inspected before a child
    /// would exist and the test needs no elevation. That the child then loads those modules is asserted end to
    /// end by <c>RunAsync_WithoutElevation_LoadsTheModulesTheAppsScriptsUse</c> in
    /// <c>SysManager.IntegrationTests</c>.</para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_BuildsAnOutOfProcessRunspace_WhateverTheElevation(bool isElevated)
    {
        bool? remote = null;
        var opened = 0;
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: () => isElevated,
            trustedPowerShellModulePath:
                @"C:\Program Files\WindowsPowerShell\Modules;" +
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
            openRunspace: runspace =>
            {
                opened++;
                remote = runspace.RunspaceIsRemote;
                return Task.FromException(new PSInvalidOperationException("Stopped before the child starts."));
            });

        await Assert.ThrowsAsync<RuntimeException>(() => runner.RunAsync("'never-runs'"));

        Assert.Equal(1, opened);
        Assert.True(remote,
            "the runner built an in-process runspace, which loads Microsoft.PowerShell.Core and nothing else, so "
            + "every script that uses a Utility, Management, Appx or Defender cmdlet fails and returns nothing");
    }

    // ── A session that stops working (#2608) ───────────────────────────────

    /// <summary>
    /// A runspace that is not open under a run surfaces as the <see cref="RuntimeException"/> every service handles.
    /// </summary>
    /// <remarks>
    /// <c>BeginInvoke</c> throws <see cref="InvalidRunspaceStateException"/> for a runspace that is not open, which is
    /// what a session whose child process has died turns into. That type derives from <see cref="SystemException"/>,
    /// so it went past the services' <c>catch (RuntimeException)</c> and the tabs' <c>catch
    /// (InvalidOperationException)</c> alike, to the app-wide handler. An open that does nothing reaches the same
    /// state without killing anything.
    /// </remarks>
    [Fact]
    public async Task RunAsync_OnARunspaceThatIsNotOpen_ThrowsTheRuntimeExceptionServicesHandle()
    {
        var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            openRunspace: _ => Task.CompletedTask,
            createRunspace: () => (runspace, null, null));

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => runner.RunAsync("'never-runs'"));

        Assert.IsType<InvalidRunspaceStateException>(ex.InnerException);
    }

    /// <summary>
    /// Cancelling only asks for the stop: <c>Cancel()</c> returns before the pipeline has stopped.
    /// </summary>
    /// <remarks>
    /// The registration used to call <c>PowerShell.Stop()</c> itself, on the thread that called <c>Cancel()</c>, and
    /// <c>Stop()</c> waits for the stop to land. On a session whose child process had died that took until the
    /// transport gave up: 58 seconds, with the app frozen for all of it when Cancel was pressed. The stop is handed to
    /// the scheduler instead, which here holds it until the test runs it, so the order is decided by the test and
    /// not by timing: the loop's own deadline is 20 seconds away, so a run that had finished would mean the stop ran
    /// inside <c>Cancel()</c>. The deadline is there so a stop that never lands cannot hang the suite.
    /// </remarks>
    [Fact]
    public async Task Cancel_AsksForTheStop_WithoutWaitingForThePipelineToStop()
    {
        var stops = new List<Action>();
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            openRunspace: runspace => Task.Run(runspace.Open),
            createRunspace: () => (RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2()), null, null),
            scheduleStop: stop =>
            {
                lock (stops) stops.Add(stop);
                return Task.CompletedTask;
            });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.LineReceived += line =>
        {
            if (line.Text == "started") started.TrySetResult();
        };
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            "'started'; $deadline = [DateTime]::UtcNow.AddSeconds(20); "
            + "while ([DateTime]::UtcNow -lt $deadline) { [System.Threading.Thread]::Sleep(10) }",
            cancellationToken: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        cts.Cancel();

        Action stop;
        lock (stops) stop = Assert.Single(stops);
        Assert.False(run.IsCompleted, "the pipeline stopped inside Cancel(), so a stop that hangs freezes the caller");

        stop();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
    }

    /// <summary>
    /// A cached runspace whose child process is gone is rebuilt rather than run on.
    /// </summary>
    /// <remarks>
    /// The runspace goes on reading Opened for about a minute after its child dies, and a run started on it in that
    /// minute waited the whole minute and then failed (#2608). A <see cref="Process"/> object with no process behind
    /// it is the case this suite can build without starting one: <c>HasExited</c> throws for it, and that counts as
    /// gone. A child that has really exited is in the integration suite.
    /// </remarks>
    [Fact]
    public async Task ACachedRunspaceWhoseChildProcessIsGone_IsRebuilt()
    {
        var built = 0;
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            openRunspace: runspace => Task.Run(runspace.Open),
            createRunspace: () =>
            {
                built++;
                return (RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2()), null, new Process());
            });

        await runner.RunAsync("2 + 2");
        Assert.Equal(4, (int)(await runner.RunAsync("2 + 2"))[0].BaseObject);

        Assert.Equal(2, built);
    }

    /// <summary>
    /// The next run does not wait for the session it replaces to be torn down.
    /// </summary>
    /// <remarks>
    /// Disposing a runspace whose child has died takes about a minute, and the lease used to do it before building the
    /// replacement: with the check above alone, the next run measured 60 seconds against a killed child and then
    /// succeeded. Here the old session's teardown is held until the end of the test, so the second run can only
    /// finish if it did not wait for it. The bounded waits are there for a failing run, not for the assertions.
    /// </remarks>
    [Fact]
    public async Task ReplacingASessionWhoseChildIsGone_DoesNotWaitForItsTeardown()
    {
        var teardownMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var built = 0;
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            openRunspace: runspace => Task.Run(runspace.Open),
            createRunspace: () =>
            {
                built++;
                return built == 1
                    ? (RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2()),
                       new HeldDisposable(teardownStarted, teardownMayFinish.Task), new Process())
                    : (RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2()), null, null);
            });

        try
        {
            await runner.RunAsync("2 + 2");
            // From another thread: the lease runs inside the call until its first real wait, so a teardown that held it
            // up would hold up this line too, and the bounded wait below would never be reached.
            var second = Task.Run(() => runner.RunAsync("3 + 3"));
            await teardownStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(6, (int)(await second.WaitAsync(TimeSpan.FromSeconds(30)))[0].BaseObject);
        }
        finally
        {
            teardownMayFinish.TrySetResult();
        }
    }

    private static PowerShellRunner CreateRunnerWithOpenFailure(Exception exception, bool isElevated)
    {
        var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
        return new PowerShellRunner(
            action => Task.Run(action),
            isElevated: () => isElevated,
            trustedPowerShellModulePath:
                @"C:\Program Files\WindowsPowerShell\Modules;" +
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
            openRunspace: _ => Task.FromException(exception),
            createRunspace: () => (runspace, null, null));
    }

    private sealed class RecordingDisposable(
        string name,
        ICollection<string> order,
        Exception? exception = null) : IDisposable
    {
        public void Dispose()
        {
            order.Add(name);
            if (exception is not null)
                throw exception;
        }
    }

    /// <summary>A teardown that says it started and then waits to be let go.</summary>
    /// <remarks>
    /// For at most two minutes, which is longer than a test using it waits for anything. With an equal bound, a
    /// teardown that held up the run gave up first, the run then finished inside the test's own wait, and the test
    /// passed against the very defect it is there to catch.
    /// </remarks>
    private sealed class HeldDisposable(TaskCompletionSource started, Task mayFinish) : IDisposable
    {
        public void Dispose()
        {
            started.TrySetResult();
            mayFinish.Wait(TimeSpan.FromMinutes(2));
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("powershell.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("pwsh")]
    [InlineData("pwsh.exe")]
    public void ApplyTrustedPowerShellModulePath_ElevatedPowerShellChild_ReplacesInheritedValue(
        string executable)
    {
        const string inheritedPath = @"C:\Users\Example\Documents\WindowsPowerShell\Modules";
        const string trustedPath = @"C:\Program Files\WindowsPowerShell\Modules";
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        startInfo.Environment["PSModulePath"] = inheritedPath;

        PowerShellRunner.ApplyTrustedPowerShellModulePath(
            startInfo,
            isElevated: true,
            trustedPath);

        Assert.Equal(trustedPath, startInfo.Environment["PSModulePath"]);
    }

    [Theory]
    [InlineData(false, "powershell.exe")]
    [InlineData(true, "cmd.exe")]
    public void ApplyTrustedPowerShellModulePath_NonPrivilegedBoundary_PreservesInheritedValue(
        bool isElevated,
        string executable)
    {
        const string inheritedPath = @"C:\Users\Example\Documents\WindowsPowerShell\Modules";
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        startInfo.Environment["PSModulePath"] = inheritedPath;

        PowerShellRunner.ApplyTrustedPowerShellModulePath(
            startInfo,
            isElevated,
            @"C:\Program Files\WindowsPowerShell\Modules");

        Assert.Equal(inheritedPath, startInfo.Environment["PSModulePath"]);
    }

    [Fact]
    public async Task RunProcessAsync_PreCancelled_DoesNotStartProcess()
    {
        var runner = new PowerShellRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunProcessAsync(
                "must-not-start.exe",
                string.Empty,
                cancellation.Token));
    }

    [Fact]
    public async Task RunProcessAsync_CancelledWhileStartQueued_DoesNotStartProcess()
    {
        var startQueued = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PowerShellRunner(async startProcess =>
        {
            startQueued.TrySetResult(true);
            await releaseStart.Task;
            startProcess();
        });
        using var cancellation = new CancellationTokenSource();

        var runTask = runner.RunProcessAsync(
            "must-not-start.exe",
            string.Empty,
            cancellation.Token);

        await startQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        releaseStart.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunProcessWithShellAsync_PreCancelled_DoesNotStartProcess()
    {
        var runner = new PowerShellRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunProcessWithShellAsync(
                "must-not-start.exe",
                string.Empty,
                cancellation.Token));
    }

    [Fact]
    public async Task RunProcessWithShellAsync_CancelledWhileStartQueued_DoesNotStartProcess()
    {
        var startQueued = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PowerShellRunner(async startProcess =>
        {
            startQueued.TrySetResult(true);
            await releaseStart.Task;
            startProcess();
        });
        using var cancellation = new CancellationTokenSource();

        var runTask = runner.RunProcessWithShellAsync(
            "must-not-start.exe",
            string.Empty,
            cancellation.Token);

        await startQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        releaseStart.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public void LineReceived_CanSubscribeAndUnsubscribe()
    {
        var runner = new PowerShellRunner();
        var received = false;
        void Handler(Models.PowerShellLine _) => received = true;
        runner.LineReceived += Handler;
        runner.LineReceived -= Handler;
        Assert.False(received);
    }

    [Fact]
    public void ProgressChanged_CanSubscribeAndUnsubscribe()
    {
        var runner = new PowerShellRunner();
        var received = false;
        void Handler(int _) => received = true;
        runner.ProgressChanged += Handler;
        runner.ProgressChanged -= Handler;
        Assert.False(received);
    }
}
