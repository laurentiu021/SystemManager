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

    [Fact]
    public async Task RunAsync_ElevatedHostOpenFailure_IsNormalizedToRuntimeException()
    {
        var runner = CreateRunnerWithOpenFailure(
            new PSInvalidOperationException("Windows PowerShell was blocked."));

        var exception = await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        Assert.Contains(
            "Windows PowerShell 5.1 is unavailable",
            exception.Message,
            StringComparison.Ordinal);
        Assert.IsType<PSInvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task RunAsync_ElevatedTransportFailure_IsNormalizedToRuntimeException()
    {
        var runner = CreateRunnerWithOpenFailure(
            new System.Management.Automation.Remoting.PSRemotingTransportException(
                "The child-process transport failed."));

        var exception = await Assert.ThrowsAsync<RuntimeException>(
            () => runner.RunAsync("'never-runs'"));

        Assert.Contains(
            "Windows PowerShell 5.1 is unavailable",
            exception.Message,
            StringComparison.Ordinal);
        Assert.IsType<System.Management.Automation.Remoting.PSRemotingTransportException>(
            exception.InnerException);
    }

    [Fact]
    public async Task RunAsync_ElevatedHostCreationFailure_IsNormalizedToRuntimeException()
    {
        var failure = new PSInvalidOperationException(
            "Windows PowerShell is unavailable.");
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => true,
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

    [Fact]
    public async Task RunAsync_ElevatedHostRegistrationFailure_IsNormalizedToRuntimeException()
    {
        var registrationFailure = new System.Security.SecurityException(
            "Windows PowerShell registration is inaccessible.");
        var failure = new TypeInitializationException(
            typeof(PowerShellProcessInstance).FullName,
            registrationFailure);
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => true,
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

    [Fact]
    public async Task RunAsync_ElevatedHostOpenFailure_DisposesInjectedProcessResources()
    {
        var order = new List<string>();
        var processInstance = new RecordingDisposable("process instance", order);
        var process = new RecordingDisposable("process", order);
        var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => true,
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
    /// The exception MAPPING beside it is gated on <c>_isElevated</c>, because only the elevated path uses an
    /// out-of-process host whose failures need translating. The timeout is not, and this asserts that: the
    /// out-of-process path is where a hang has been observed, but an unbounded wait is the wrong behaviour on
    /// either path, and gating it would leave the in-process one with no answer at all.
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

    [Fact]
    public async Task DefenderStatus_ElevatedHostOpenFailure_ReturnsUnavailable()
    {
        var runner = CreateRunnerWithOpenFailure(
            new PSInvalidOperationException("Windows PowerShell was blocked."));
        var service = new DefenderService(runner);

        var status = await service.GetStatusAsync();

        Assert.False(status.Available);
    }

    [Fact]
    public async Task DnsStatus_ElevatedHostOpenFailure_ReturnsUnavailable()
    {
        var runner = CreateRunnerWithOpenFailure(
            new PSInvalidOperationException("Windows PowerShell was blocked."));
        using var service = new DnsService(runner);

        var status = await service.GetCurrentDnsAsync();

        Assert.Equal("Unavailable", status);
    }

    private static PowerShellRunner CreateRunnerWithOpenFailure(Exception exception)
    {
        var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
        return new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => true,
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
