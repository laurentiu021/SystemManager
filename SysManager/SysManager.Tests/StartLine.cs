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
    /// Generous enough never to trip on a loaded CI runner, and short enough to fail rather than hang. Shared
    /// with a race that has a rendezvous of its own inside the writers.
    /// </summary>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Starts every writer and releases them together once all have reached the line, then waits for all of
    /// them. A fault in a writer is rethrown here. A writer that has not returned within <see cref="Bound"/>
    /// fails the race with a <see cref="TimeoutException"/>.
    /// </summary>
    internal static async Task RaceAsync(params Action[] writers)
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
        await Task.WhenAll(running).WaitAsync(Bound);
    }
}
