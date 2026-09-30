// SysManager · PerformanceServiceExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the new PerformanceService features: restore point creation,
/// RAM working set trim, and hibernation toggle.
/// Focuses on testable logic; actual system calls are integration-level.
/// </summary>
public class PerformanceServiceExtendedTests
{
    // ── ReadHibernationEnabled ──

    [Fact]
    public void ReadHibernationEnabled_ReturnsBoolean()
    {
        var result = PerformanceService.ReadHibernationEnabled();
        Assert.IsType<bool>(result);
    }

    // ── TrimWorkingSets ──
    // Driven with unassociated Process objects and a trim call of the test's own. The real call trims every process on
    // the machine, this test host included, and two tests here used to make it on every run of the suite (#2557). The
    // integration suite makes the real EmptyWorkingSet call, on its own process alone.

    [Fact]
    public void TrimWorkingSets_CountsTheProcessesWhoseTrimSucceeded()
    {
        var answers = new Queue<bool>([true, false, true]);

        var trimmed = PerformanceService.TrimWorkingSets(NewProcesses(3), _ => answers.Dequeue());

        Assert.Equal(2, trimmed);
        Assert.Empty(answers);   // one trim per process
    }

    [Fact]
    public void TrimWorkingSets_SkipsAProcessItCannotOpenOrThatHasExited()
    {
        // A protected process refuses its handle with a Win32Exception; one that has exited throws
        // InvalidOperationException. Neither stops the rest.
        var outcomes = new Queue<Func<bool>>([
            () => throw new Win32Exception(5),
            () => throw new InvalidOperationException("The process has exited."),
            () => true]);

        var trimmed = PerformanceService.TrimWorkingSets(NewProcesses(3), _ => outcomes.Dequeue()());

        Assert.Equal(1, trimmed);
        Assert.Empty(outcomes);
    }

    [Fact]
    public void TrimWorkingSets_DisposesEveryProcessItIsGiven()
    {
        // Each Process from GetProcesses owns a handle, and the one whose trim threw must be let go of too.
        TrackedProcess[] processes = [new(), new(), new()];
        var calls = 0;

        PerformanceService.TrimWorkingSets(processes,
            _ => ++calls == 2 ? throw new InvalidOperationException("The process has exited.") : true);

        Assert.All(processes, p => Assert.True(p.IsDisposed));
    }

    /// <summary>
    /// A Process that records its disposal. Process.Dispose does not raise Component.Disposed, so the event cannot
    /// show it.
    /// </summary>
    private sealed class TrackedProcess : Process
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void TrimWorkingSets_OnAServiceBuiltWithoutAProcessList_TrimsNothing()
    {
        // The test constructor's default. The public one passes every process on the machine, and a test that leaves
        // the list out must get none rather than that.
        var trims = 0;
        var service = NewService(processes: null, trim: _ => { trims++; return true; });

        Assert.Equal(0, service.TrimWorkingSets());
        Assert.Equal(0, trims);
    }

    [Fact]
    public void TrimWorkingSets_OnAService_TrimsTheProcessesItWasBuiltWith()
    {
        var service = NewService(processes: () => NewProcesses(2), trim: _ => true);

        Assert.Equal(2, service.TrimWorkingSets());
    }

    [Fact]
    public void TrimWorkingSets_AsTheAppBuildsTheService_GoesThroughEveryProcessWithTheRealCall()
    {
        // The wiring the tests above cannot run. Built the way the app builds it, the list is every process on the
        // machine and the trim is EmptyWorkingSet: checked by which methods the two delegates point at, never by
        // calling them, since calling them is exactly what the unit suite must not do. Without this, dropping the list
        // from the public constructor would leave Trim RAM trimming nothing, with every test still green.
        var services = new ServiceCollection();
        services.ConfigureServices();
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<PerformanceService>();

        Assert.Equal(typeof(Process).GetMethod(nameof(Process.GetProcesses), Type.EmptyTypes),
            Delegate<Func<Process[]>>(service, "_processes").Method);
        Assert.Equal(typeof(PerformanceService).GetMethod(nameof(PerformanceService.TrimWorkingSet),
                BindingFlags.NonPublic | BindingFlags.Static),
            Delegate<Func<Process, bool>>(service, "_trimWorkingSet").Method);
    }

    private static T Delegate<T>(PerformanceService service, string field) where T : Delegate =>
        Assert.IsType<T>(typeof(PerformanceService)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service));

    private static Process[] NewProcesses(int count) =>
        Enumerable.Range(0, count).Select(_ => new Process()).ToArray();

    private static PerformanceService NewService(Func<Process[]>? processes, Func<Process, bool> trim)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        return new PerformanceService(ps, new RestorePointService(ps),
            Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N")), processes, trim);
    }

    // ── Service construction ──

    [Fact]
    public void Service_AcceptsPowerShellRunner()
    {
        // The folder overload: the two-argument one resolves the real profile for its snapshot (#2555).
        var ps = new PowerShellRunner();
        var service = new PerformanceService(ps, new RestorePointService(ps),
            Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N")));
        Assert.NotNull(service);
    }
}
