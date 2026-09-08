// SysManager · StaHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Threading;

namespace SysManager.IntegrationTests;

/// <summary>
/// Runs an action on the suite's single STA thread and waits for it. Required for tests that touch
/// WPF Application / Dispatcher: xUnit's own runner is MTA.
/// </summary>
/// <remarks>
/// <para><b>One thread for the whole suite, not one per call.</b> This used to start a fresh STA thread
/// per <see cref="Run"/> and <c>Join</c> it, which let the thread exit. That produced five failures the
/// moment the suite ran to completion: <c>AppResources.Ensure()</c> creates the <c>Application</c> on
/// whichever thread reaches it first, the brushes and styles in its <c>Resources</c> are
/// <c>DispatcherObject</c>s owned by that thread, and every later view instantiation then resolved
/// app-scope resources belonging to a thread that was not merely different but already gone —
/// <c>InvalidOperationException: The calling thread cannot access this object because a different thread
/// owns it</c>, surfacing as a <c>XamlParseException</c> on the first Style or BorderBrush (#2156).</para>
/// <para><b>The thread pumps.</b> <see cref="Dispatcher.Run"/> makes this a real UI thread rather than an
/// STA thread with a dispatcher nobody drains. That matters beyond the resource ownership: work posted
/// with <c>InvokeAsync</c> or <c>BeginInvoke</c> now actually runs, so the suite exercises the same
/// marshalling behaviour the application has instead of a degenerate version of it.</para>
/// <para><b>What is traded away.</b> The per-call <c>Join</c> was also the isolation: a thread that dies
/// takes its timers and pending operations with it. On a shared pumped thread a <c>DispatcherTimer</c>
/// started by one test keeps ticking, and operations queued by an earlier test run before the current
/// action rather than being discarded. Both are deliberate — the second is stricter ordering, not looser
/// — but a test that leaves work on the dispatcher can now be observed by the next one, which was
/// previously impossible. That is the cost of having resources a second test can use at all.</para>
/// <para><b>The synchronous Invoke is correct here</b>, unlike in the application, where ten of them were
/// removed for blocking on a dispatcher nothing pumped (#2152). The contract of this method is "run it
/// there and wait", the dispatcher IS pumped by construction, and <c>Invoke</c> rethrows the action's
/// exception on the caller's thread — which is what the old <c>captured</c>-and-rethrow did by hand.</para>
/// </remarks>
public static class StaHelper
{
    private static readonly Lazy<Dispatcher> Sta = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Starts the STA thread and returns its dispatcher once the thread is pumping.
    /// </summary>
    /// <remarks>
    /// The handshake is required, not defensive: <c>Dispatcher.CurrentDispatcher</c> has to be read ON the
    /// new thread — reading it from here would create and return a dispatcher for the CALLING thread, and
    /// every <c>Invoke</c> would then run the action on the MTA test thread while appearing to work. The
    /// thread is a background thread so it cannot keep the test host alive after the run.
    /// </remarks>
    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            Name = "SysManager integration STA",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        _thread = thread;
        return ready.Task.GetAwaiter().GetResult();
    }

    private static Thread? _thread;

    /// <summary>
    /// Ends the dispatcher and waits briefly for the thread. Called once, when the collection that owns
    /// every STA test has finished; safe to call when the thread was never started.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists at all.</b> A pumping dispatcher performs real layout passes over the views
    /// the tests built, and <c>TextBlock.MeasureOverride</c> reaches DirectWrite through
    /// <c>FontFamily</c>'s static initialiser. A background thread is killed where it stands when the
    /// runtime tears down, so one inside that native call as the host exits takes the process down with
    /// <c>0xC0000005</c> — after every test has already reported, which is the most confusing possible
    /// place for it. Measured: six view tests passed and then the host died on the way out.</para>
    /// <para><b>Why a collection fixture and not <c>ProcessExit</c>.</b> That was the first attempt and it
    /// replaced the crash with a hang: shutting a dispatcher down from a <c>ProcessExit</c> handler asks
    /// WPF to run work on a thread the runtime is already tearing down, and the host stopped exiting at
    /// all. Measured too — 12 green and then a timeout instead of an exit. The end of the collection is the
    /// right moment: every test has reported, and the runtime is still healthy enough to shut a dispatcher
    /// down properly.</para>
    /// <para>The join is bounded. A wedged dispatcher must not turn a completed run into a hung one — the
    /// results are already in by this point, so giving up costs nothing that matters.</para>
    /// </remarks>
    internal static void Stop()
    {
        if (!Sta.IsValueCreated) return;

        Sta.Value.InvokeShutdown();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Sta.Value.Invoke(action);
    }
}
