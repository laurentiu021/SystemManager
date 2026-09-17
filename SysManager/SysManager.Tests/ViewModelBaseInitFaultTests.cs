// SysManager · ViewModelBaseInitFaultTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net.Http;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The boundary that catches a constructor's fire-and-forget init keeps its promise for EVERY exception
/// type, not the six it happened to name.
/// </summary>
/// <remarks>
/// <c>RunInitAsync</c>'s summary promised that "exceptions are caught and logged instead of becoming
/// unobserved task exceptions", and nothing ever tested it. It caught six types; everything else escaped —
/// and escaping was invisible, because nothing in production awaits <c>InitializationComplete</c>, so the
/// fault sat on a task that is rooted by that very property and therefore never finalized. The
/// <c>TaskScheduler.UnobservedTaskException</c> handler <c>App</c> wires up fires from the finalizer, so it
/// never ran. The tab just stayed half-loaded (#2258).
/// <para>How it was found is the reason these tests exist at all: making one unrelated test await the init
/// turned two long-passing tests red with a <c>NullReferenceException</c> from
/// <c>DebloaterService.ParsePackages</c> that had been thrown on every launch. The only way to notice was to
/// await a task production never awaits.</para>
/// <para>Asserted through <see cref="ViewModelBase.InitializationFault"/> rather than through the log,
/// because reading what Serilog wrote means assigning the global <c>Log.Logger</c> that every test in the
/// run shares — which <c>LogServiceLogDirTests</c> spells out as the thing not to do. The property is also
/// the stronger assertion: it says which exception was caught, not merely that a line was written.</para>
/// </remarks>
public sealed class ViewModelBaseInitFaultTests
{
    /// <summary>
    /// A view model whose only job is to run the init it is handed. <paramref name="rethrow"/> pins
    /// <see cref="ViewModelBase.RethrowsUnexpectedInitFaults"/> so each branch is testable without
    /// attaching a debugger or mutating anything process-wide.
    /// </summary>
    private sealed class ProbeViewModel : ViewModelBase
    {
        private readonly bool? _rethrow;

        internal ProbeViewModel(Func<Task> init, bool? rethrow = null)
        {
            // Assigned before the init starts, which matters: RunInitAsync reads the property in its catch,
            // and a synchronous throw from init reaches that catch before this constructor returns.
            _rethrow = rethrow;
            InitializeAsync(init);
        }

        protected internal override bool RethrowsUnexpectedInitFaults
            => _rethrow ?? base.RethrowsUnexpectedInitFaults;
    }

    /// <summary>An init that faults after yielding, which is the shape a real one has.</summary>
    private static Func<Task> Faulting(Exception ex) => async () =>
    {
        await Task.Yield();
        throw ex;
    };

    /// <summary>
    /// The types the six-entry list missed. Each was reachable from a real init path: the
    /// <see cref="NullReferenceException"/> is the one that actually happened, and the rest are what a
    /// service parsing WMI output, registry values or PowerShell results throws when its input is not the
    /// shape it assumed.
    /// </summary>
    public static TheoryData<Exception> UnexpectedFaults() =>
    [
        new NullReferenceException("a parsed object was null"),
        new ArgumentException("a value the service built itself was rejected"),
        new KeyNotFoundException("a property the query was assumed to return"),
        new InvalidCastException("a WMI value of a different type"),
        new FormatException("a number that was not one"),
        new System.ComponentModel.Win32Exception("a native call the wrapper did not expect to fail"),
        new System.Management.ManagementException("a WMI class this Windows build does not carry"),
    ];

    [Theory]
    [MemberData(nameof(UnexpectedFaults))]
    public async Task AnUnexpectedFault_IsCaughtAndRecorded(Exception thrown)
    {
        using var vm = new ProbeViewModel(Faulting(thrown), rethrow: false);

        // Before the final catch existed this await threw, which is the whole defect: the ONLY way to see
        // the fault was to await a task production never awaits.
        await vm.InitializationComplete;

        Assert.Same(thrown, vm.InitializationFault);
    }

    /// <summary>
    /// The five types that already had a handler keep it, and are recorded the same way. Without this, the
    /// fix could quietly reroute them through the new broad catch and log them at Fatal instead of Error.
    /// </summary>
    public static TheoryData<Exception> ExpectedFaults() =>
    [
        new InvalidOperationException("a collection changed under the enumerator"),
        new UnauthorizedAccessException("a registry key this user cannot read"),
        new IOException("a file another process holds"),
        new HttpRequestException("the update check could not reach the network"),
        new TimeoutException("a query that did not return"),
    ];

    [Theory]
    [MemberData(nameof(ExpectedFaults))]
    public async Task AnExpectedFault_IsStillCaughtAndRecorded(Exception thrown)
    {
        using var vm = new ProbeViewModel(Faulting(thrown), rethrow: false);

        await vm.InitializationComplete;

        Assert.Same(thrown, vm.InitializationFault);
    }

    /// <summary>
    /// An expected fault is never rethrown, whatever the rethrow decision says. The seam exists for faults
    /// nobody predicted; a handled one has already been dealt with, and rethrowing it would turn a tab that
    /// merely could not reach the network into a developer-visible crash.
    /// </summary>
    [Fact]
    public async Task AnExpectedFault_IsNotRethrownEvenWhenUnexpectedOnesAre()
    {
        var thrown = new TimeoutException("a query that did not return");
        using var vm = new ProbeViewModel(Faulting(thrown), rethrow: true);

        await vm.InitializationComplete;

        Assert.Same(thrown, vm.InitializationFault);
    }

    [Fact]
    public async Task ASynchronousThrow_IsCaughtToo()
    {
        // asyncAction() throwing BEFORE it returns a Task is a different path from a task that faults: the
        // exception is raised by the invocation itself, inside the try rather than at the await.
        var thrown = new NullReferenceException("thrown before the first await");
        using var vm = new ProbeViewModel(() => throw thrown, rethrow: false);

        await vm.InitializationComplete;

        Assert.Same(thrown, vm.InitializationFault);
    }

    [Fact]
    public async Task Cancellation_IsNotRecordedAsAFault()
    {
        // A tab closing mid-load is the expected path. Recording it would make the property true for
        // almost every tab on shutdown and drown the faults that matter.
        using var vm = new ProbeViewModel(Faulting(new OperationCanceledException()), rethrow: false);

        await vm.InitializationComplete;

        Assert.Null(vm.InitializationFault);
    }

    [Fact]
    public async Task ASuccessfulInit_RecordsNoFault()
    {
        using var vm = new ProbeViewModel(() => Task.CompletedTask);

        await vm.InitializationComplete;

        Assert.Null(vm.InitializationFault);
    }

    [Fact]
    public async Task WithRethrowOn_TheFaultReachesWhoeverAwaits()
    {
        var thrown = new NullReferenceException("a parsed object was null");
        using var vm = new ProbeViewModel(Faulting(thrown), rethrow: true);

        var surfaced = await Assert.ThrowsAsync<NullReferenceException>(() => vm.InitializationComplete);

        Assert.Same(thrown, surfaced);
        // Recorded before the rethrow, not instead of it — a developer build gets both.
        Assert.Same(thrown, vm.InitializationFault);
    }

    /// <summary>
    /// The default is "only under a debugger", and it is pinned because getting it wrong is asymmetric:
    /// a default of <c>true</c> would make an unforeseen fault in any of the 40 init paths take down a
    /// released build, which is strictly worse than the silent half-load this issue set out to fix.
    /// </summary>
    [Fact]
    public void TheDefaultDecision_RethrowsOnlyUnderADebugger()
    {
        using var vm = new ProbeViewModel(() => Task.CompletedTask);

        Assert.Equal(System.Diagnostics.Debugger.IsAttached, vm.RethrowsUnexpectedInitFaults);
    }
}
