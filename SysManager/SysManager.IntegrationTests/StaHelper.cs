// SysManager · StaHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;

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
/// <para><b>A work queue, NOT a pumped dispatcher, and that distinction is the whole design.</b> The
/// obvious way to share one STA thread is <c>Dispatcher.Run()</c> plus <c>Dispatcher.Invoke</c>, and it was
/// tried three ways. All three failed, each somewhere new: with no shutdown the run ended in an access
/// violation at process exit; shutting down from <c>ProcessExit</c> replaced that with a hang; shutting
/// down from a collection fixture passed locally and then crashed the CI test host mid-test, after 329
/// tests and with 284 never run. The common factor was pumping. A pumping dispatcher schedules real layout
/// passes over the views the tests built, <c>TextBlock.MeasureOverride</c> reaches DirectWrite through
/// <c>FontFamily</c>'s static initialiser, and on a headless runner that dies with
/// <c>0xC0000005</c>.</para>
/// <para>What #2156 actually needs is that the thread owning the resources STAYS ALIVE. It does not need a
/// message loop. So the thread drains a <see cref="BlockingCollection{T}"/> instead, and nothing schedules
/// layout. <c>DispatcherTimer</c> and <c>InvokeAsync</c> remain as inert here as they were with a thread
/// per call — that is the status quo, deliberately preserved, and it is why this cannot regress anything
/// the old version supported.</para>
/// <para><b>No shutdown hook, and none needed.</b> The thread is a background thread parked on
/// <c>GetConsumingEnumerable</c>, so the runtime can discard it at exit with nothing in flight to
/// interrupt. It was the in-flight native call that made the pumped version unsafe to abandon.</para>
/// <para><b>What is traded away.</b> The per-call <c>Join</c> was also the isolation: a thread that dies
/// takes anything it was holding with it. On a shared thread, state a test leaves in a thread-static or in
/// <c>Application.Current.Resources</c> is visible to the next one. <c>Application.Current</c> was already
/// process-wide, so that part is unchanged; what is new is that a second test can USE it, which is the
/// point.</para>
/// </remarks>
public static class StaHelper
{
    private static readonly BlockingCollection<Action> Queue = new();

    private static readonly Lazy<Thread> Sta = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Thread Start()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable())
                work();
        })
        {
            Name = "SysManager integration STA",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    /// <summary>
    /// Queues <paramref name="action"/> onto the STA thread, waits for it, and rethrows whatever it threw.
    /// </summary>
    /// <remarks>
    /// The capture-and-rethrow is what the thread-per-call version did after its <c>Join</c>, kept
    /// deliberately: a swallowed test failure is the worst outcome available here, so the exception has to
    /// reach the caller as itself rather than wrapped in something the assertion cannot see through.
    /// </remarks>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        _ = Sta.Value;   // starts the thread on first use

        using var done = new ManualResetEventSlim(initialState: false);
        Exception? captured = null;

        Queue.Add(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        });

        done.Wait();
        if (captured is not null) throw captured;
    }
}
