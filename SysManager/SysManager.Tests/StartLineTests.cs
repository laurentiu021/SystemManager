// SysManager · StartLineTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;

namespace SysManager.Tests;

/// <summary>
/// The start line every race test in this suite releases its writers from. It is a test helper, but the
/// property that stops those races failing on a busy runner is behaviour, and a later edit could quietly
/// undo it. Hence these tests.
/// </summary>
public sealed class StartLineTests
{
    [Fact]
    public async Task EveryWriter_RunsOnAThreadOfItsOwn_NotOnThePool()
    {
        // The whole point. A writer that runs on a pool thread cannot reach the line while the pool is busy,
        // and that is how two unrelated races failed in one CI run.
        var onThePool = new ConcurrentBag<bool>();

        await StartLine.RaceAsync(
            () => onThePool.Add(Thread.CurrentThread.IsThreadPoolThread),
            () => onThePool.Add(Thread.CurrentThread.IsThreadPoolThread),
            () => onThePool.Add(Thread.CurrentThread.IsThreadPoolThread));

        Assert.Equal(new[] { false, false, false }, onThePool);
    }

    [Fact]
    public async Task AWriterThatThrows_FailsTheRace()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartLine.RaceAsync(
            () => throw new InvalidOperationException("the losing writer"),
            () => { }));

        Assert.Equal("the losing writer", ex.Message);
    }

    [Fact]
    public async Task ARaceNeedsAtLeastTwoWriters()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StartLine.RaceAsync(() => { }));
    }

    // ── a race that runs out of time (#2548) ──
    // The race used to fail the moment its bound ran out, with the writers still running. The test's cleanup then
    // deleted the folder one of them was writing to, and that threw a second exception on top of the timeout. The
    // first wait here is tiny, and the slow writer is held on a gate the test opens, so the outcome does not depend
    // on timing.

    private static readonly TimeSpan AtOnce = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task ARaceThatRunsOutOfTime_FailsOnlyOnceItsWritersHaveStopped()
    {
        var ranOut = new TaskCompletionSource();
        using var finish = new ManualResetEventSlim(false);

        var race = StartLine.RaceAsync(AtOnce, StartLine.Bound, ranOut.SetResult, () => finish.Wait(), () => { });
        await ranOut.Task.WaitAsync(StartLine.Bound);

        // The writer is held, so the race cannot have failed yet. A quarter of a second is ample for one that
        // failed at once to show it.
        Assert.NotSame(race, await Task.WhenAny(race, Task.Delay(TimeSpan.FromMilliseconds(250))));
        finish.Set();
        await Assert.ThrowsAsync<TimeoutException>(() => race);
    }

    [Fact]
    public async Task ARaceThatRunsOutOfTime_StillFails_WhenAWriterNeverStops()
    {
        // Not an event: this writer is still waiting when the test ends, and an event cannot be disposed under it.
        var released = new TaskCompletionSource();
        try
        {
            var race = StartLine.RaceAsync(AtOnce, AtOnce, onTimedOut: null, () => released.Task.Wait(), () => { });

            // Bounded here rather than by a timeout of its own, so a race that hangs fails this test rather than
            // passing it.
            Assert.Same(race, await Task.WhenAny(race, Task.Delay(StartLine.Bound)));
            await Assert.ThrowsAsync<TimeoutException>(() => race);
        }
        finally
        {
            released.SetResult();   // the writer's thread is let go once the race has failed
        }
    }

    [Fact]
    public async Task AWriterThatFaultsAfterTheTimeout_DoesNotReplaceIt()
    {
        // The timeout is what failed. A writer that throws after it was reported instead would hide that.
        using var ranOut = new ManualResetEventSlim(false);

        await Assert.ThrowsAsync<TimeoutException>(() => StartLine.RaceAsync(AtOnce, StartLine.Bound, ranOut.Set,
            () => { ranOut.Wait(); throw new InvalidOperationException("the losing writer, late"); },
            () => { }));
    }
}
