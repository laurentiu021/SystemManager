// SysManager · StartLine — one place that draws the line a race test releases its writers from
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Tests;

/// <summary>
/// Releases a race test's writers together, each on a thread of its own, and waits for all of them, bounded.
/// </summary>
/// <remarks>
/// Every race in this suite used to draw the line itself, and always the same way. Each writer:
/// <list type="bullet">
///   <item>started with <see cref="Task.Run(Action)"/>;</item>
///   <item>signalled a <see cref="CountdownEvent"/>;</item>
///   <item>then parked on a gate until the test thread had seen every signal.</item>
/// </list>
/// The parked writer held a pool thread, and so did the test thread waiting for the signals. When the pool had
/// no thread to spare, the writers could not start at all. In one CI run, three races and a DNS lookup were all
/// waiting on the pool, and no test finished for 16 seconds. One race attempt waited out its whole 30-second
/// bound, and two unrelated races failed with "the racing writers never reached the start line", while the
/// code under test was fine.
/// <para>A writer here runs on a thread created for it, through
/// <see cref="TaskCreationOptions.LongRunning"/>. Reaching the line waits only for that thread to start, and
/// parking at the line takes nothing from the pool.</para>
/// <para>The bound stays, so a writer that never returns still fails the run instead of hanging it.
/// <see cref="ArchitectureTests.EveryRaceStartLine_IsDrawnInOnePlace"/> keeps the line here.</para>
/// </remarks>
internal static class StartLine
{
    /// <summary>
    /// Generous for the races as they are sized, and short enough to fail rather than hang. Shared with a race
    /// that has a rendezvous of its own inside the writers.
    /// </summary>
    /// <remarks>
    /// Not "never trips": the activity-log race ran out of it twice, on CI runners many times slower than usual. That
    /// race was cut down to fit rather than the bound raised for every race (#2548).
    /// </remarks>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Starts every writer and releases them together once all have reached the line, then waits for all of
    /// them. A fault in a writer is rethrown here. A writer that has not returned within <see cref="Bound"/>
    /// fails the race with a <see cref="TimeoutException"/>, after up to one more <see cref="Bound"/> for the
    /// writers to stop.
    /// </summary>
    internal static Task RaceAsync(params Action[] writers) => RaceAsync(Bound, Bound, onTimedOut: null, writers);

    /// <summary>
    /// The race with its two waits and a hook for the moment the first runs out, so a test can drive that moment.
    /// The start line always waits <see cref="Bound"/>.
    /// </summary>
    /// <param name="toFinish">How long the writers have to return.</param>
    /// <param name="toStop">How much longer the race waits for them after that, before it fails.</param>
    /// <param name="onTimedOut">Called when <paramref name="toFinish"/> runs out, before the second wait.</param>
    /// <param name="writers">The racing writers, two or more.</param>
    /// <remarks>
    /// When the first wait runs out the writers are still running, and the test's cleanup comes next. A folder
    /// deleted under a writer made the cleanup throw a second exception on top of the timeout, which read as a
    /// second fault (#2548). So the race waits for its writers again before it fails with the timeout. A writer
    /// that never returns still fails the run instead of hanging it, <paramref name="toStop"/> later.
    /// </remarks>
    internal static async Task RaceAsync(TimeSpan toFinish, TimeSpan toStop, Action? onTimedOut, params Action[] writers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(writers.Length, 2);

        using var ready = new CountdownEvent(writers.Length);
        using var go = new ManualResetEventSlim(false);
        var running = writers
            .Select(write => Task.Factory.StartNew(
                () => { ready.Signal(); go.Wait(); write(); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        try
        {
            Assert.True(ready.Wait(Bound), "the racing writers never reached the start line");
        }
        finally
        {
            // Released even when the line was not reached, so that no writer is left parked on the gate.
            go.Set();
        }

        // WaitAsync throws TimeoutException on the bound and rethrows any writer fault. Neither a hang nor an
        // exception inside a writer is swallowed.
        var all = Task.WhenAll(running);
        try
        {
            await all.WaitAsync(toFinish);
        }
        catch (TimeoutException)
        {
            onTimedOut?.Invoke();
            // The timeout is what failed. A writer that faults after it is observed here, not rethrown in its place.
            if (await Task.WhenAny(all, Task.Delay(toStop)) == all)
                _ = all.Exception;
            throw;
        }
    }
}
