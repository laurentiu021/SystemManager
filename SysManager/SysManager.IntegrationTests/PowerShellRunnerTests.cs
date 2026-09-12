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
    /// A script that blocks, can be interrupted, and always ends by itself.
    /// </summary>
    /// <remarks>
    /// Three properties, each load-bearing and each learned the hard way.
    /// <para><b>It yields every 50 ms</b>, so <c>ps.Stop()</c> has a statement boundary to interrupt at.
    /// <c>Start-Sleep -Seconds 30</c> gives it none — Stop interrupts BETWEEN pipeline statements, never
    /// inside one blocking cmdlet call — so the sleep ran to completion every time. The controlled comparison
    /// is in the CI log for #2206: on the same elevated out-of-process runspace, this loop cancelled in 2.2 s
    /// while the sleep took 31.4 s.</para>
    /// <para><b>It has a DEADLINE of its own.</b> This was <c>while ($true)</c>, which has none, and that is
    /// the whole defect: a cancel that failed to land left the script running until CI killed the entire job
    /// at its 30-minute ceiling — three times on 2026-09-11, each time reporting nothing at all about the
    /// other 635 tests (#2263). The bound does not weaken what is measured: every caller asserts cancellation
    /// lands in a few SECONDS, so 20 turns an all-or-nothing hang into an ordinary failure carrying its
    /// diagnosis.</para>
    /// <para><b><c>[DateTime]::UtcNow</c> rather than <c>Get-Date</c></b>, and <c>Thread::Sleep</c> rather
    /// than <c>Start-Sleep</c>: the unelevated transport builds its runspace from
    /// <c>InitialSessionState.CreateDefault2()</c>, which loads <c>Microsoft.PowerShell.Core</c> ONLY. Both
    /// cmdlets live in <c>Microsoft.PowerShell.Utility</c> and would error instantly, returning normally — so
    /// a cancellation that never happened would read as a fast success. A .NET static call needs no module.
    /// </para>
    /// </remarks>
    private const string BlockingScript =
        "$deadline = [DateTime]::UtcNow.AddSeconds(20); "
        + "while ([DateTime]::UtcNow -lt $deadline) { [System.Threading.Thread]::Sleep(50) }";

    /// <summary>
    /// Awaits <paramref name="work"/>, failing the test if it does not finish rather than waiting forever.
    /// </summary>
    /// <remarks>
    /// <see cref="BlockingScript"/> bounds the SCRIPT; this bounds everything else. A cancellation test's
    /// natural shape is "await the thing that should have been interrupted", and if the runner itself never
    /// returns then the await never returns either — which is a 30-minute job timeout that says nothing,
    /// instead of one named failure (#2263). The ceiling is generous on purpose: it is a backstop for a hang,
    /// not the measurement, and each caller asserts its own much tighter elapsed bound.
    /// </remarks>
    private static async Task<Exception?> BoundedAsync(ValueTask<Exception?> work, string testName)
    {
        // AsTask because a ValueTask may be awaited only once and Task.WhenAny needs to hold on to it.
        var task = work.AsTask();
        var ceiling = TimeSpan.FromSeconds(60);

        if (await Task.WhenAny(task, Task.Delay(ceiling)).ConfigureAwait(false) != task)
            Assert.Fail($"{testName} did not finish within {ceiling.TotalSeconds:F0}s. The script it runs "
                        + "ends itself after 20s, so the run never returning points at the runner rather "
                        + "than at the script — and hanging here would cost the whole suite.");

        return await task.ConfigureAwait(false);
    }

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

    /// <summary>
    /// Cancelling a running script must stop it, and must surface that as
    /// <see cref="OperationCanceledException"/> so a caller's cancel branch fires.
    /// </summary>
    /// <remarks>
    /// This test has failed on CI five times with the elapsed time sitting on the script's own duration —
    /// 31.12, 30.565, 30.664, 33.82, 32.03 seconds against a 30-second sleep, and nothing in between. So the
    /// failure is all-or-nothing: either <c>ps.Stop()</c> takes effect within a few hundred milliseconds or
    /// the pipeline runs to its natural end (#2206). The obvious cause — the cancellation callback landing on
    /// a not-yet-invoked <c>PowerShell</c> instance — was refuted with a harness that mutated the runner three
    /// ways and cancelled in ~0.35 s every time, so no speculative fix was shipped.
    ///
    /// <para><b>What this instrumentation is for.</b> The failure message used to be one number, which cannot
    /// tell the two live hypotheses apart. The runner's contract makes them distinguishable:
    /// <c>ps.Stop()</c> makes <c>EndInvoke</c> throw <c>PipelineStoppedException</c>, which
    /// <c>RunOnLeasedRunspaceAsync</c> translates to <c>OperationCanceledException</c>. So if Stop never
    /// takes effect the script completes, <c>EndInvoke</c> returns normally and <b>no exception is thrown at
    /// all</b>.</para>
    ///
    /// <list type="bullet">
    /// <item>slow + <c>OperationCanceledException</c> → Stop worked and the 5-second bound is simply tight.</item>
    /// <item>slow + <b>no exception</b> → Stop had no effect; the pipeline ran to completion. A user pressing
    /// Cancel would wait out the whole operation.</item>
    /// <item>token never fired → the <c>CancellationTokenSource</c> itself was starved, which on a loaded
    /// runner is a test-environment fact rather than a product defect.</item>
    /// </list>
    ///
    /// <para>The token's own firing time is captured through a SECOND registration on the same token rather
    /// than by instrumenting the runner: it needs no production change, and it separates "the token fired
    /// late" from "the token fired on time and was ignored", which is the whole question.</para>
    ///
    /// <para>Error lines are captured because of a trap this bug already produced twice: on a dev box
    /// <c>Start-Sleep</c> does not exist in this runner's runspace — <c>CreateDefault2()</c> loads
    /// <c>Microsoft.PowerShell.Core</c> only — so the script errors instantly, returns normally, and a
    /// cancellation that never happened reads as a fast success. If that is what is happening, the captured
    /// error says so instead of leaving the next person to rediscover it.</para>
    /// </remarks>
    [Fact]
    public async Task RunAsync_SupportsCancellation()
    {
        var runner = new PowerShellRunner();
        var lines = new System.Collections.Concurrent.ConcurrentQueue<Models.PowerShellLine>();
        runner.LineReceived += lines.Enqueue;

        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Elapsed at the moment the token fired, from a registration of our own. -1 means it never fired.
        var firedAtMs = -1.0;
        using var observer = cts.Token.Register(() => firedAtMs = sw.Elapsed.TotalMilliseconds);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var ex = await BoundedAsync(
            Record.ExceptionAsync(async () => await runner.RunAsync(
                BlockingScript, cancellationToken: cts.Token)),
            nameof(RunAsync_SupportsCancellation));
        sw.Stop();

        var errors = lines.Where(l => l.Kind == Models.OutputKind.Error).Select(l => l.Text).ToList();
        var diagnosis =
            $"elapsed {sw.Elapsed}; token fired at {(firedAtMs < 0 ? "NEVER" : $"{firedAtMs:F0} ms")}; "
            + $"exception {ex?.GetType().Name ?? "NONE"} ({ex?.Message ?? "-"}); {lines.Count} line(s) captured"
            + (errors.Count > 0 ? $", errors: {string.Join(" | ", errors.Take(2))}" : "");

        // Stopping without reporting it is its own defect: every caller's cancel branch is written around
        // OperationCanceledException, so a silent return would show a cancelled operation as a completed one.
        // Asserted FIRST, because the two below describe what KIND of cancellation it was and neither means
        // anything if there was none.
        Assert.True(ex is OperationCanceledException, "Cancellation did not surface as OperationCanceledException. " + diagnosis);

        // The message, not the clock, and this is the assertion that actually names the defect.
        //
        // BlockingScript is a LOOP of 50 ms sleeps, and ps.Stop() interrupts a pipeline between statements —
        // the blocking-call test below measured this exact shape cancelling in 2.2 s. So "completed on its
        // own" here does NOT mean "PowerShell cannot interrupt a blocking call", which is the legitimate
        // outcome that test pins. It means the stop was LOST: the registration fired while the instance was
        // still NotStarted, Stop() threw InvalidOperationException, it was swallowed, and BeginInvoke then
        // ran the script in full (#2286). The runner now re-asserts Stop after BeginInvoke to close that.
        //
        // This replaces a bare `elapsed < 5s`, which was the same defect measured through a proxy: it could
        // only see a lost stop when the loss also happened to be slow, and when it fired it reported "took
        // too long", which reads as a loaded runner. Two of the three explanations its own message offered
        // were about timing, and the true one was not.
        Assert.Contains("stopped by cancellation", ex!.Message, StringComparison.Ordinal);

        // The bound is KEPT, below the script's own 20 seconds so a lost stop cannot pass it, and well above
        // the ~350 ms a working stop takes. It is now a backstop rather than the discriminator: if it ever
        // fires while the message above passes, the stop worked and the runner got slow, which is a
        // different finding and the message says which.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            "Cancellation was reported as a real stop but took too long. " + diagnosis);
    }

    /// <summary>
    /// A single long BLOCKING call cannot be interrupted — but the cancellation must still be reported when
    /// it finally returns.
    /// </summary>
    /// <remarks>
    /// The real limit behind #2206, pinned rather than wished away. <c>ps.Stop()</c> interrupts a pipeline
    /// between statements; it cannot interrupt one statement that is inside a blocking native call. The
    /// evidence is a controlled comparison from one CI run, on one elevated out-of-process runspace:
    /// <c>while ($true) { Thread::Sleep(50) }</c> cancelled in 2.2 s, while <c>Start-Sleep -Seconds 30</c>
    /// took 31.4 s — the whole sleep. Same transport, same cancel, opposite outcome.
    ///
    /// <para>So the canary above asserted something PowerShell does not promise, and cost thirty seconds
    /// per CI run to say so. What the runner CAN promise is that the cancellation is not lost, and that is
    /// what this asserts: the call takes the script's full duration, and it still raises
    /// <c>OperationCanceledException</c> rather than returning as though nothing had been asked.</para>
    ///
    /// <para>Three seconds of real waiting, deliberately. A shorter block would not reliably outlast the
    /// cancel on a loaded runner, and the value being pinned is precisely that the call does NOT come back
    /// early. <c>Thread::Sleep</c> rather than <c>Start-Sleep</c> because the latter needs
    /// <c>Microsoft.PowerShell.Utility</c>, which the in-process runspace does not load — it would fail
    /// instantly on a dev box and pin nothing at all.</para>
    ///
    /// <para><b>Either cancellation message is correct here, and finding out why corrected the model above.</b>
    /// Running this locally reported "stopped by cancellation" after the full three seconds: the stop stays
    /// PENDING through the blocking call and takes effect at the next statement boundary, so
    /// <c>EndInvoke</c> throws after all. CI's 30-second <c>Start-Sleep</c> reported "completed on its own"
    /// because the sleep WAS the whole script — there was no next statement to stop at, so the pipeline
    /// simply finished. Three outcomes, then, not two: interrupted promptly, stopped late at a boundary, or
    /// never noticed. Asserting one message would pin the transport rather than the contract, and the
    /// contract is that the cancellation reaches the caller either way.</para>
    /// </remarks>
    [Fact]
    public async Task RunAsync_CancellingASingleBlockingCall_CannotInterruptItButStillReportsIt()
    {
        var runner = new PowerShellRunner();
        var warm = await runner.RunAsync("1 + 1");
        Assert.Equal(2, Convert.ToInt32(Assert.Single(warm).BaseObject, System.Globalization.CultureInfo.InvariantCulture));

        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var ex = await Record.ExceptionAsync(async () => await runner.RunAsync(
            "[System.Threading.Thread]::Sleep(3000)", cancellationToken: cts.Token));
        sw.Stop();

        var diagnosis = $"elapsed {sw.Elapsed}; exception {ex?.GetType().Name ?? "NONE"} ({ex?.Message ?? "-"})";

        // The premise: the call really did outlast the cancel. If this ever stops holding, ps.Stop() has
        // become able to interrupt a blocking call and the rest of this test is about nothing.
        Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(2500),
            "the blocking call returned early, so ps.Stop() now interrupts inside one — which would be good "
            + "news and makes this test obsolete rather than failing. " + diagnosis);

        Assert.True(ex is OperationCanceledException,
            "a cancelled run whose script could not be interrupted returned without reporting the "
            + "cancellation, so the caller sees a completed operation. " + diagnosis);

        // Either arm is right — see the remarks. What must not happen is neither.
        Assert.True(
            ex!.Message.Contains("stopped by cancellation", StringComparison.Ordinal)
            || ex.Message.Contains("completed on its own", StringComparison.Ordinal),
            "the cancellation was reported by neither of the runner's two arms, so the message no longer "
            + "says which path ran and the next failure of this kind is back to being a stopwatch reading. "
            + diagnosis);
    }

    /// <summary>
    /// A cancel that lands while the RUNSPACE IS STILL OPENING must still report cancellation — the script
    /// never runs, and returning an empty result set silently makes that look like a successful query.
    /// </summary>
    /// <remarks>
    /// The regression test for #2206's root cause, and the only one of the three here that pins it
    /// deterministically.
    /// <para><b>Why the other two cannot.</b> Both depend on how long a runspace takes to open. Run alone in
    /// a cold process that is ~300 ms, dominated by assembly loading, so a 300 ms cancel lands during the
    /// lease and takes this path. Run after any other test in the class it is ~20 ms, the pipeline is already
    /// executing, and the cancel takes the <c>PipelineStoppedException</c> path instead. Measured: removing
    /// the post-await throw leaves the whole class GREEN, while running
    /// <see cref="RunAsync_SupportsCancellation"/> on its own against the same code fails with
    /// "exception NONE". A test whose coverage depends on execution order is not a regression test.</para>
    /// <para><b>How this one is deterministic.</b> The <c>openRunspace</c> seam blocks until the test has
    /// cancelled, so the token is guaranteed to be already cancelled when <c>Register</c> runs — which
    /// invokes the callback synchronously, putting <c>ps.Stop()</c> on a <c>NotStarted</c> instance, where it
    /// throws <c>InvalidOperationException</c> and is swallowed. No timing, no sleeping, and the same
    /// sequence on every machine.</para>
    /// <para><b>Corrected:</b> this said <c>BeginInvoke</c>/<c>EndInvoke</c> then complete "without running
    /// the script". They do not — the script RUNS, and against <c>1 + 1</c> that is simply invisible. The
    /// test below runs the same seam with a script that blocks for 20 seconds and measured exactly that: the
    /// full 20 seconds before the fix in #2286, about a second after it. The claim was untestable here and
    /// wrong there.</para>
    /// <para>The MESSAGE is asserted, not just the exception type. Both cancellation paths raise
    /// <c>OperationCanceledException</c>, so accepting either would let this pass on the wrong one — and the
    /// wrong one is the path that already worked.</para>
    /// </remarks>
    [Fact]
    public async Task RunAsync_CancelledWhileTheRunspaceIsOpening_ReportsCancellation()
    {
        using var cancelled = new SemaphoreSlim(0);   // released once the test has cancelled the token
        using var reachedOpen = new SemaphoreSlim(0); // signals that the lease has begun opening

        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => false,
            openRunspace: async runspace =>
            {
                reachedOpen.Release();
                await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Run(runspace.Open);
            });

        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync("1 + 1", cancellationToken: cts.Token);

        Assert.True(await reachedOpen.WaitAsync(TimeSpan.FromSeconds(10)),
            "the runspace open was never reached, so nothing below is about the opening window.");
        await cts.CancelAsync();
        cancelled.Release();

        var ex = await Record.ExceptionAsync(() => run);

        Assert.True(ex is OperationCanceledException,
            $"a run cancelled during the runspace open reported {ex?.GetType().Name ?? "NO EXCEPTION"}. "
            + "With no exception the caller receives an empty result set and no indication that anything was "
            + "cancelled — for a query that is indistinguishable from a real answer of \"nothing found\".");

        // EITHER discriminator is correct here now, and that is a change from what this asserted.
        //
        // It pinned "completed on its own", which was the honest description of a hole: the registration
        // fired while the instance was NotStarted, Stop() threw InvalidOperationException, it was swallowed,
        // and BeginInvoke went on to run the script with nothing having asked it to stop. The runner now
        // re-asserts Stop after BeginInvoke and closes that window (#2286), so this scenario reaches the
        // pipeline properly.
        //
        // Which message comes back is then genuinely racy — and only because THIS script is `1 + 1`. Against
        // a trivial script the stop and the script's own completion are in a real race, and both outcomes
        // are correct: cancellation is reported either way, which is the contract asserted above and the
        // reason this test exists. Pinning the winner of that race is precisely what made
        // RunAsync_SupportsCancellation flaky, so it is not repeated here.
        //
        // The deterministic pin lives there instead, where the script BLOCKS for 20 seconds: a lost stop
        // cannot hide behind a fast script, so "stopped by cancellation" is the only correct answer and is
        // asserted exactly.
        Assert.True(ex!.Message.Contains("stopped by cancellation", StringComparison.Ordinal)
                    || ex.Message.Contains("completed on its own", StringComparison.Ordinal),
            "the cancellation was reported without either discriminator in its message, so a future reader "
            + $"cannot tell which path ran: \"{ex.Message}\"");
    }

    /// <summary>
    /// A run cancelled while the runspace is opening must not go on to RUN the script.
    /// </summary>
    /// <remarks>
    /// The test above proves the cancellation is reported. It cannot prove the script did not run, because
    /// its script is <c>1 + 1</c> — indistinguishable from not running at all. This one uses the same
    /// deterministic seam with a script that BLOCKS for 20 seconds, which separates the two outright: a lost
    /// stop takes the full 20 seconds, a delivered one returns in about a second.
    /// <para>That distinction is the whole of #2286. CI saw a cancel at 390 ms against a looping script and
    /// the run finish its entire 20 seconds reporting "not interrupted … completed on its own". Since a loop
    /// of 50 ms sleeps IS interruptible — the blocking-call test below measures that same shape cancelling in
    /// 2.2 s — the stop was not refused, it was lost in the NotStarted window: <c>Register</c> fires
    /// synchronously, <c>Stop()</c> throws <c>InvalidOperationException</c> on a not-yet-invoked instance, it
    /// is swallowed, and <c>BeginInvoke</c> starts a pipeline nothing has asked to stop.</para>
    /// <para>Deterministic for the same reason as the test above — the seam holds the open until the test has
    /// cancelled — so this needs no sleeping to arrange the race and cannot depend on execution order. It is
    /// the regression test the fix would otherwise not have: removing the runner's post-BeginInvoke
    /// re-assertion takes this from about a second to the script's full 20.</para>
    /// </remarks>
    [Fact]
    public async Task RunAsync_CancelledWhileTheRunspaceIsOpening_DoesNotRunTheScriptAnyway()
    {
        using var cancelled = new SemaphoreSlim(0);
        using var reachedOpen = new SemaphoreSlim(0);

        using var runner = new PowerShellRunner(
            action => Task.Run(action),
            isElevated: static () => false,
            openRunspace: async runspace =>
            {
                reachedOpen.Release();
                await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Run(runspace.Open);
            });

        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = runner.RunAsync(BlockingScript, cancellationToken: cts.Token);

        Assert.True(await reachedOpen.WaitAsync(TimeSpan.FromSeconds(10)),
            "the runspace open was never reached, so nothing below is about the opening window.");
        await cts.CancelAsync();
        cancelled.Release();

        var ex = await BoundedAsync(Record.ExceptionAsync(() => run),
                                    nameof(RunAsync_CancelledWhileTheRunspaceIsOpening_DoesNotRunTheScriptAnyway));
        sw.Stop();

        var diagnosis = $"elapsed {sw.Elapsed}; exception {ex?.GetType().Name ?? "NONE"} ({ex?.Message ?? "-"})";

        Assert.True(ex is OperationCanceledException, "the run did not report cancellation at all. " + diagnosis);

        // The assertion that matters: well under the script's own 20 seconds, so a stop that was swallowed
        // and never re-asserted cannot pass. 5 seconds rather than 2 leaves room for a cold runspace open on
        // a loaded runner without leaving room for the defect.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            "a run cancelled during the runspace open went on to execute the script in full — the stop was "
            + "swallowed on a NotStarted instance and never re-asserted, so the user's Cancel did nothing "
            + "but the call still reported cancellation. " + diagnosis);
    }

    /// <summary>
    /// Cancelling a pipeline that is ALREADY RUNNING must also surface as
    /// <see cref="OperationCanceledException"/> — the other half of #2206, and the half CI keeps hitting.
    /// </summary>
    /// <remarks>
    /// The test above cancels at 300 ms, and opening a cold runspace takes about that long, so the cancel
    /// lands during the lease and <c>ps.Stop()</c> hits a not-yet-invoked instance. That is one of two paths
    /// through the same hole, and it is the only one reproducible on a dev box.
    /// <para>This one takes the other: a warm-up call first, so the runspace is already open and the lease
    /// returns immediately, then a longer delay so the cancel arrives while the pipeline is genuinely
    /// executing. <c>EndInvoke</c> then throws <c>PipelineStoppedException</c> and the translation arm is
    /// what has to convert it — a different line of the runner from the one the test above exercises.</para>
    /// <para>The script is deliberately module-free. <c>Start-Sleep</c> lives in
    /// <c>Microsoft.PowerShell.Utility</c>, which <c>CreateDefault2()</c> does not load, so on a dev box it
    /// fails instantly with "the module could not be loaded" and the script returns in milliseconds —
    /// a cancellation that never happened reading as a fast pass. That trap cost a whole round of
    /// investigation on #2206; <c>[System.Threading.Thread]::Sleep</c> needs no module and blocks for real.
    /// The assertion that the probe actually blocked is what keeps this test from repeating the mistake.</para>
    /// </remarks>
    [Fact]
    public async Task RunAsync_CancellingARunningPipeline_ReportsCancellation()
    {
        var runner = new PowerShellRunner();

        // Warm the runspace so the lease below is instant and the cancel lands mid-execution rather than
        // mid-open. Module-free, and its result proves the runspace works before anything is measured.
        var warm = await runner.RunAsync("1 + 1");
        Assert.Equal(2, Convert.ToInt32(Assert.Single(warm).BaseObject, System.Globalization.CultureInfo.InvariantCulture));

        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var firedAtMs = -1.0;
        using var observer = cts.Token.Register(() => firedAtMs = sw.Elapsed.TotalMilliseconds);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var ex = await BoundedAsync(
            Record.ExceptionAsync(async () => await runner.RunAsync(
                BlockingScript, cancellationToken: cts.Token)),
            nameof(RunAsync_CancellingARunningPipeline_ReportsCancellation));
        sw.Stop();

        var diagnosis = $"elapsed {sw.Elapsed}; token fired at {(firedAtMs < 0 ? "NEVER" : $"{firedAtMs:F0} ms")}; "
                        + $"exception {ex?.GetType().Name ?? "NONE"} ({ex?.Message ?? "-"})";

        // The probe must have BLOCKED. An infinite loop that returns in 50 ms means the script never ran,
        // and everything below it would then be asserting over nothing.
        Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(400),
            "the blocking script returned before the cancel could land, so this test proves nothing about "
            + "cancelling a RUNNING pipeline. " + diagnosis);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            "a running pipeline did not stop when cancelled. " + diagnosis);

        Assert.True(ex is OperationCanceledException,
            "a cancelled running pipeline did not report cancellation. " + diagnosis
            + " — PipelineStoppedException here means the translation arm stopped converting it, and a "
            + "message of \"completed on its own\" means Stop did not interrupt a pipeline that was "
            + "definitely running, which is the CI failure shape rather than this test's own.");
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
