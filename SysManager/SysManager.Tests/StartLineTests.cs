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
}
