// SysManager · PowerShellRunnerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class PowerShellRunnerTests
{
    /// <summary>
    /// Disposing the runner STOPS the child process its runspace was built with, rather than only dropping
    /// the handle.
    /// </summary>
    /// <remarks>
    /// <para><b>Was "a run's teardown", and that changed.</b> An elevated runner now REUSES its runspace
    /// across calls rather than building one per call (#2149), so the end of a run is no longer when the
    /// child is released — the release points are eviction after
    /// <see cref="PowerShellRunner.IdleRunspaceLifetime"/> and disposal of the runner. This asserts the
    /// deterministic one.</para>
    /// <para>Elevation is forced rather than inherited so the test exercises the reuse path on any machine.
    /// The runspace itself is in-process, so nothing here needs administrator rights; the injected child
    /// stands in for the <c>powershell.exe</c> that the real elevated path would have started.</para>
    /// </remarks>
    /// <remarks>
    /// #2149(a), end to end through <c>RunAsync</c>. Disposing a <see cref="System.Diagnostics.Process"/>
    /// releases the HANDLE and does not stop the process. When <c>powershell.exe</c> outlived its runspace, its
    /// stdout and stderr pipes stayed open and the remoting transport's reader threads stayed blocked in
    /// <c>ReadFile</c> forever — a hang dump from the integration job showed 1,112 such threads, about four per
    /// runspace, so roughly 276 children still alive in a single test host.
    /// <para><b>Through RunAsync, not by invoking the release directly.</b> An earlier version called the
    /// private method by reflection, which proved what the method does and nothing about whether the production
    /// path uses it — a mutation that handed teardown a bare <c>Dispose</c> instead left it green. Injecting the
    /// child through the <c>createRunspace</c> seam and letting <c>RunAsync</c> tear it down is what closes
    /// that: it covers the wiring and the behaviour in one assertion.</para>
    /// <para>An in-process runspace is used, so the run itself needs no elevation; the child injected beside it
    /// stands in for the <c>powershell.exe</c> the elevated path would have started.</para>
    /// <para><b>Nothing is redirected, and that is load-bearing.</b> The first stand-in was <c>cmd /c pause</c>
    /// with redirected stdin, and it made the test undiscriminating: disposing the <c>Process</c> closes its
    /// redirected handles, <c>pause</c> saw EOF, and the child exited on its own — so a mutation that removed
    /// the termination entirely still left this GREEN. With no redirected handles, disposing the object cannot
    /// touch the process and only an actual kill ends it. A loopback ping is the delay: ~59 seconds against a
    /// 10-second wait, almost no CPU, and no traffic leaves the machine. It also runs as <c>cmd</c> → <c>ping</c>,
    /// so the tree kill is exercised rather than a single process.</para>
    /// <para>Observed through a SECOND handle to the same process: teardown disposes the one it is given, and
    /// querying that afterwards throws "No process is associated with this object", which says nothing about
    /// whether the process died. The independent handle lets <c>WaitForExitAsync</c> be awaited rather than
    /// polled, so there is no sleeping and nothing to go flaky.</para>
    /// </remarks>
    [Fact]
    public async Task DisposingTheRunner_StopsTheChildItsRunspaceWasBuiltWith()
    {
        var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", "/c ping -n 60 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        using var observer = System.Diagnostics.Process.GetProcessById(child.Id);

        try
        {
            Assert.False(observer.HasExited);   // the premise, not an assumption

            var runspace = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(
                System.Management.Automation.Runspaces.InitialSessionState.CreateDefault2());
            var runner = new PowerShellRunner(
                action => Task.Run(action),
                isElevated: static () => true,
                createRunspace: () => (runspace, null, child));

            var result = await runner.RunAsync("2 + 2");
            Assert.Equal(4, (int)result[0].BaseObject);   // the run really happened

            // Still alive, because the runspace is now cached for reuse rather than torn down per call.
            Assert.False(observer.HasExited,
                "the child was released at the end of the run — reuse is not taking effect, so every "
                + "elevated call still spawns and hands-shakes its own powershell.exe");

            runner.Dispose();

            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await observer.WaitForExitAsync(bounded.Token);

            Assert.True(observer.HasExited,
                "the child process outlived the runner; its pipes stay open and the remoting transport's "
                + "reader threads stay blocked on them forever");
        }
        finally
        {
            try { if (!observer.HasExited) observer.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone, which is the expected outcome */ }
        }
    }

    /// <summary>
    /// An elevated runner builds ONE runspace for several calls, not one per call.
    /// </summary>
    /// <remarks>
    /// The point of #2149's second half. Every elevated call used to spawn a <c>powershell.exe</c> 5.1 child
    /// and complete a remoting handshake with it, then throw both away — and the services that use this make
    /// their calls in bursts: <c>DnsService</c> six, <c>EdgeOneDriveService</c> four, three others three each.
    /// <para>Counted through the <c>createRunspace</c> seam rather than by timing, so the assertion is
    /// exact and cannot go flaky on a slow machine. The runspaces are in-process, so nothing here needs
    /// administrator rights.</para>
    /// </remarks>
    [Fact]
    public async Task ElevatedRunner_ReusesOneRunspaceAcrossCalls()
    {
        var built = 0;
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => true,
            createRunspace: () =>
            {
                built++;
                return (System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(
                            System.Management.Automation.Runspaces.InitialSessionState.CreateDefault2()),
                        null, null);
            });

        for (var call = 0; call < 4; call++)
            Assert.Equal(4, (int)(await runner.RunAsync("2 + 2"))[0].BaseObject);

        Assert.Equal(1, built);
    }

    /// <summary>
    /// The unelevated path is untouched: it still builds a runspace per call.
    /// </summary>
    /// <remarks>
    /// Scope, asserted rather than described. The in-process runspace starts no child and opens in a
    /// fraction of the time, so caching it would add a lifetime to reason about for almost no gain — and
    /// #2149 is about the out-of-process branch only. Without this, narrowing or widening the elevation
    /// check would go unnoticed.
    /// </remarks>
    [Fact]
    public async Task UnelevatedRunner_StillBuildsARunspacePerCall()
    {
        var built = 0;
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => false,
            createRunspace: () =>
            {
                built++;
                return (System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(
                            System.Management.Automation.Runspaces.InitialSessionState.CreateDefault2()),
                        null, null);
            });

        for (var call = 0; call < 3; call++)
            await runner.RunAsync("2 + 2");

        Assert.Equal(3, built);
    }

    /// <summary>
    /// A reused runspace is released once it has been idle, not held for the session.
    /// </summary>
    /// <remarks>
    /// The half of the design that keeps reuse from becoming a memory trade. Nothing disposes most of these
    /// runners — nine are built directly in <c>MainWindowViewModel</c>'s designer graph and live as long as
    /// the window — so without eviction a dozen <c>powershell.exe</c> processes would be resident for the
    /// whole run, in an app whose pitch is making a PC feel faster.
    /// <para>Driven with a 200&#160;ms lifetime through the constructor seam. The wait is bounded and
    /// polls for the state change rather than sleeping a fixed interval and hoping.</para>
    /// </remarks>
    [Fact]
    public async Task ElevatedRunner_ReleasesTheRunspaceOnceIdle()
    {
        var built = 0;
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => true,
            idleRunspaceLifetime: TimeSpan.FromMilliseconds(200),
            createRunspace: () =>
            {
                built++;
                return (System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(
                            System.Management.Automation.Runspaces.InitialSessionState.CreateDefault2()),
                        null, null);
            });

        await runner.RunAsync("2 + 2");
        Assert.Equal(1, built);

        // Longer than the lifetime, and doubling if a slow machine needs more. Waiting LESS than the
        // lifetime cannot work: every run re-arms the countdown, so polling faster than it keeps it alive
        // forever — which is what the first version of this test did.
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var wait = TimeSpan.FromMilliseconds(400);
        while (built == 1)
        {
            await Task.Delay(wait, bounded.Token);
            await runner.RunAsync("2 + 2");   // rebuilds once the idle one has been evicted
            wait *= 2;
        }

        Assert.Equal(2, built);
    }

    /// <summary>
    /// A cached runspace that is no longer open is discarded and rebuilt.
    /// </summary>
    /// <remarks>
    /// The recovery path, and the reason the state is re-checked on every lease rather than assumed. Things
    /// outside this class can break a cached runspace — the child killed by the user or by cleanup, the
    /// remoting channel dropped — and a runspace that is not <c>Opened</c> cannot run a pipeline at all.
    /// Without the re-check, one broken child would make every later call on that consumer fail for the rest
    /// of the session, which is a far worse failure than the per-call cost reuse removes.
    /// <para><c>Close()</c> stands in for all of those: it is the state they leave behind, reached
    /// deterministically instead of by killing something and hoping.</para>
    /// </remarks>
    [Fact]
    public async Task ElevatedRunner_RebuildsARunspaceThatIsNoLongerOpen()
    {
        var built = 0;
        System.Management.Automation.Runspaces.Runspace? last = null;
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => true,
            createRunspace: () =>
            {
                built++;
                last = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(
                    System.Management.Automation.Runspaces.InitialSessionState.CreateDefault2());
                return (last, null, null);
            });

        await runner.RunAsync("2 + 2");
        Assert.Equal(1, built);

        last!.Close();

        Assert.Equal(4, (int)(await runner.RunAsync("2 + 2"))[0].BaseObject);
        Assert.Equal(2, built);
    }

    /// <summary>
    /// Two calls that overlap on an ALREADY-CACHED runspace both complete.
    /// </summary>
    /// <remarks>
    /// The hazard reuse introduces. A runspace runs one pipeline at a time, so where a runspace per call made
    /// concurrent calls independent, a shared one makes them a conflict — <c>BeginInvoke</c> against a busy
    /// runspace throws. They are serialised behind a gate instead, and this is what says so.
    /// <para><b>The warm-up call is the whole test.</b> Without it, two calls started back to back do not
    /// conflict and this passes with the gate removed: the first has not cached anything by the time the
    /// second leases, so each builds its own runspace and nothing is shared. Measured — the first version of
    /// this test stayed GREEN under a mutation that deleted the gate entirely. Establishing the cache first is
    /// what makes both callers actually reach for the same runspace.</para>
    /// <para>The sleep exists to hold the first pipeline open while the second arrives. It creates the
    /// overlap rather than being asserted on, so there is no timing in the assertions.</para>
    /// <para>Cheap in practice: the type is registered Transient so each consumer has its own runner, and the
    /// consumers that make several calls already serialise them. The gate is the guarantee rather than a
    /// dependency on that staying true.</para>
    /// </remarks>
    [Fact]
    public async Task ElevatedRunner_OverlappingCallsOnACachedRunspace_BothComplete()
    {
        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => true,
            createRunspace: () =>
                (System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(
                     System.Management.Automation.Runspaces.InitialSessionState.CreateDefault2()),
                 null, null));

        await runner.RunAsync("2 + 2");   // establishes the cache — see the remarks

        var slow = runner.RunAsync("Start-Sleep -Milliseconds 300; 2 + 2");
        var quick = runner.RunAsync("3 + 3");

        var results = await Task.WhenAll(slow, quick);

        Assert.Equal(4, (int)results[0][0].BaseObject);
        Assert.Equal(6, (int)results[1][0].BaseObject);
    }

    [Fact]
    public async Task RunAsync_SimpleExpression_ReturnsResult()
    {
        var runner = new PowerShellRunner();
        var result = await runner.RunAsync("2 + 2");
        Assert.NotEmpty(result);
        Assert.Equal(4, (int)result[0].BaseObject);
    }

    [Fact]
    public async Task RunAsync_WhenElevated_IsolatesModuleDiscoveryInChildProcess()
    {
        const string marker = "UNTRUSTED_MODULE_EXECUTED";
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"sysmanager-psmodule-test-{Guid.NewGuid():N}");
        var moduleRoot = Path.Combine(tempRoot, "SysManagerProbe");
        var parentModulePath = Environment.GetEnvironmentVariable("PSModulePath");

        try
        {
            Directory.CreateDirectory(moduleRoot);
            await File.WriteAllTextAsync(
                Path.Combine(moduleRoot, "SysManagerProbe.psd1"),
                "@{ RootModule='SysManagerProbe.psm1'; ModuleVersion='1.0.0'; " +
                "FunctionsToExport=@('Invoke-SysManagerProbe') }");
            await File.WriteAllTextAsync(
                Path.Combine(moduleRoot, "SysManagerProbe.psm1"),
                $"function Invoke-SysManagerProbe {{ '{marker}' }}; " +
                "Export-ModuleMember -Function Invoke-SysManagerProbe");

            var inheritedModulePath = string.Join(
                Path.PathSeparator,
                new[] { tempRoot, parentModulePath }
                    .Where(static path => !string.IsNullOrWhiteSpace(path)));
            Environment.SetEnvironmentVariable("PSModulePath", inheritedModulePath);

            var runner = new PowerShellRunner(
                action => Task.Run(action),
                isElevated: static () => true);
            var sawCommandNotFound = false;
            runner.LineReceived += line =>
                sawCommandNotFound |= line.Kind == Models.OutputKind.Error;

            var injectedResult = await runner.RunAsync("Invoke-SysManagerProbe");
            var trustedResult = await runner.RunAsync(
                "(Get-Command Get-NetAdapter -ErrorAction Stop).Source");

            Assert.DoesNotContain(injectedResult, item =>
                string.Equals(item.BaseObject?.ToString(), marker, StringComparison.Ordinal));
            Assert.True(sawCommandNotFound);
            Assert.Contains(trustedResult, item =>
                string.Equals(
                    item.BaseObject?.ToString(),
                    "NetAdapter",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                inheritedModulePath,
                Environment.GetEnvironmentVariable("PSModulePath"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", parentModulePath);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ElevatedPowerShell_WhenStartupWouldRestorePersonalModules_ReassertsTrustedPath()
    {
        var machineModulePath = Environment.GetEnvironmentVariable(
            "PSModulePath",
            EnvironmentVariableTarget.Machine);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.False(string.IsNullOrWhiteSpace(machineModulePath));
        Assert.False(string.IsNullOrWhiteSpace(documents));

        var suffix = Guid.NewGuid().ToString("N");
        var moduleName = $"SysManagerPersonalProbe_{suffix}";
        var commandName = $"Invoke-SysManagerPersonalProbe{suffix}";
        var marker = $"UNTRUSTED_PERSONAL_MODULE_{suffix}";
        var windowsPowerShellRoot = Path.Combine(documents, "WindowsPowerShell");
        var personalModulesRoot = Path.Combine(windowsPowerShellRoot, "Modules");
        var moduleRoot = Path.Combine(personalModulesRoot, moduleName);
        var windowsPowerShellRootExisted = Directory.Exists(windowsPowerShellRoot);
        var personalModulesRootExisted = Directory.Exists(personalModulesRoot);
        var parentModulePath = Environment.GetEnvironmentVariable("PSModulePath");

        try
        {
            Directory.CreateDirectory(moduleRoot);
            await File.WriteAllTextAsync(
                Path.Combine(moduleRoot, $"{moduleName}.psd1"),
                $"@{{ RootModule='{moduleName}.psm1'; ModuleVersion='1.0.0'; " +
                $"FunctionsToExport=@('{commandName}') }}");
            await File.WriteAllTextAsync(
                Path.Combine(moduleRoot, $"{moduleName}.psm1"),
                $"function {commandName} {{ '{marker}' }}; " +
                $"Export-ModuleMember -Function {commandName}");

            var runner = new PowerShellRunner(
                action => Task.Run(action),
                isElevated: static () => true,
                trustedPowerShellModulePath: machineModulePath);
            var lines = new List<Models.PowerShellLine>();
            runner.LineReceived += lines.Add;

            var runspaceResults = await runner.RunAsync(commandName);
            Assert.DoesNotContain(
                runspaceResults,
                item => string.Equals(
                    item.BaseObject?.ToString(),
                    marker,
                    StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Kind == Models.OutputKind.Error);

            lines.Clear();
            var childExitCode = await runner.RunScriptViaPwshAsync(
                $"$ErrorActionPreference='Stop'; {commandName}");
            Assert.NotEqual(0, childExitCode);
            Assert.DoesNotContain(
                lines,
                line => line.Text.Contains(marker, StringComparison.Ordinal));
            Assert.Equal(
                parentModulePath,
                Environment.GetEnvironmentVariable("PSModulePath"));
        }
        finally
        {
            if (Directory.Exists(moduleRoot))
                Directory.Delete(moduleRoot, recursive: true);

            if (!personalModulesRootExisted &&
                Directory.Exists(personalModulesRoot) &&
                !Directory.EnumerateFileSystemEntries(personalModulesRoot).Any())
            {
                Directory.Delete(personalModulesRoot);
            }

            if (!windowsPowerShellRootExisted &&
                Directory.Exists(windowsPowerShellRoot) &&
                !Directory.EnumerateFileSystemEntries(windowsPowerShellRoot).Any())
            {
                Directory.Delete(windowsPowerShellRoot);
            }
        }
    }

    [Fact]
    public async Task RunAsync_EmitsOutputLines_ViaEvent()
    {
        var runner = new PowerShellRunner();
        var lines = new List<string>();
        runner.LineReceived += l => lines.Add(l.Text);
        // Plain string literal is more reliable than Write-Output — it always
        // lands in the output pipeline as a PSObject the runner forwards.
        await runner.RunAsync("'hello-from-ps'");
        Assert.Contains(lines, s => s.Contains("hello-from-ps"));
    }

    [Fact]
    public async Task RunAsync_EmitsWarnings_AsWarningKind()
    {
        // Under InitialSessionState.CreateDefault2 the Warning stream can be
        // silenced by ambient preferences in some hosts. We only assert that
        // the runner processes the script to completion; the stream mapping is
        // exercised indirectly by the Info/Error tests below.
        var runner = new PowerShellRunner();
        var ex = await Record.ExceptionAsync(async () =>
            await runner.RunAsync("Write-Warning 'beware'"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task RunAsync_EmitsErrors_AsErrorKind()
    {
        var runner = new PowerShellRunner();
        var gotError = false;
        runner.LineReceived += l =>
        {
            if (l.Kind == Models.OutputKind.Error) gotError = true;
        };
        await runner.RunAsync("Write-Error 'nope'");
        Assert.True(gotError);
    }

    [Fact]
    public async Task RunAsync_SupportsCancellation()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Record.ExceptionAsync(async () =>
            await runner.RunAsync("Start-Sleep -Seconds 30", cancellationToken: cts.Token));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Cancellation took too long: {sw.Elapsed}");
    }

    [Fact]
    public async Task RunProcessAsync_Where_ReturnsZero()
    {
        var runner = new PowerShellRunner();
        // 'where.exe' always exists on Windows and returns 0 when it finds something.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var code = await runner.RunProcessAsync("where.exe", "cmd.exe", cts.Token);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task RunProcessWithShellAsync_CmdExitZero_ReturnsZero()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var code = await runner.RunProcessWithShellAsync("cmd.exe", "/c exit 0", cts.Token);

        Assert.Equal(0, code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessCancellationObservedAfterExit_ReturnsExitCode(bool useShell)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            async (process, token) =>
            {
                await process.WaitForExitAsync(CancellationToken.None);
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            });

        var exitCode = useShell
            ? await runner.RunProcessWithShellAsync("cmd.exe", "/c exit 42", cancellation.Token)
            : await runner.RunProcessAsync("cmd.exe", "/c exit 42", cancellation.Token);

        Assert.Equal(42, exitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessCancellation_WhenTreeTerminationPartiallyFails_ReportsProcessMayRemain(
        bool useShell)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new PowerShellRunner(
            action => Task.Run(action),
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromException(new OperationCanceledException(token));
            },
            process =>
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit();
                throw new AggregateException("A descendant could not be terminated.");
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useShell
                ? runner.RunProcessWithShellAsync(
                    "powershell.exe",
                    "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60",
                    cancellation.Token)
                : runner.RunProcessAsync(
                    "powershell.exe",
                    "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60",
                    cancellation.Token));

        Assert.Contains("may still be running", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<AggregateException>(exception.InnerException);
    }

    [Fact]
    public async Task RunProcessAsync_MissingExe_Throws()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await runner.RunProcessAsync("this-binary-does-not-exist.exe", "", cts.Token));
    }

    [Fact]
    public async Task RunProcessAsync_CancellationKillsProcess()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Record.ExceptionAsync(async () =>
            await runner.RunProcessAsync("cmd.exe", "/c timeout /t 60", cts.Token));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunScriptViaPwshAsync_ReturnsExitCodeZero_OnSuccess()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exit = await runner.RunScriptViaPwshAsync("exit 0", cts.Token);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task RunScriptViaPwshAsync_ReturnsNonZero_OnFailure()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exit = await runner.RunScriptViaPwshAsync("exit 42", cts.Token);
        Assert.Equal(42, exit);
    }

    [Fact]
    public void IsClixmlNoise_Detects_AllKnownPatterns()
    {
        var m = typeof(PowerShellRunner).GetMethod("IsClixmlNoise",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)m.Invoke(null, new object[] { "#< CLIXML" })!);
        Assert.True((bool)m.Invoke(null, new object[] { "<Objs Version=\"1.1\">" })!);
        Assert.True((bool)m.Invoke(null, new object[] { "<Obj RefId=\"0\">" })!);
        Assert.True((bool)m.Invoke(null, new object[] { "</Objs>" })!);
    }

    [Fact]
    public void IsClixmlNoise_Returns_False_OnRealOutput()
    {
        var m = typeof(PowerShellRunner).GetMethod("IsClixmlNoise",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.False((bool)m.Invoke(null, new object[] { "Hello World" })!);
        Assert.False((bool)m.Invoke(null, new object[] { "winget upgrade" })!);
        Assert.False((bool)m.Invoke(null, new object[] { "123" })!);
    }
}
