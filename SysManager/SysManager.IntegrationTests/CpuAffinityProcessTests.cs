// SysManager · CpuAffinityProcessTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// CPU Core Affinity's reads and changes against a real process: a child this test starts, so pinning it changes
/// nothing outside the test.
/// </summary>
/// <remarks>
/// The unit suite reads its own process and writes it only the mask it already has. A change that really lands, and
/// one that really does not, need a process that can be changed (#2514).
/// </remarks>
public sealed class CpuAffinityProcessTests
{
    /// <summary>Only core 0: a change on any machine with two or more logical CPUs.</summary>
    private const long CoreZero = 0b1;

    [Fact]
    public async Task TrySetAffinity_AtTheListedStartTime_PinsTheChild()
    {
        SkipOnASingleCpu();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var child = await RunningChild.StartAsync(heldFile: null, bounded.Token);

        var changed = new CpuAffinityService().TrySetAffinity(child.Id, child.StartTime, CoreZero, out var error);

        Assert.True(changed, error);
        Assert.Equal(CoreZero, MaskOf(child));
    }

    [Fact]
    public async Task TrySetAffinity_ForAProcessThatStartedAtAnotherTime_LeavesTheChildAsItWas()
    {
        SkipOnASingleCpu();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var child = await RunningChild.StartAsync(heldFile: null, bounded.Token);
        var before = MaskOf(child);
        Assert.NotEqual(CoreZero, before);   // the premise: pinning it would show

        var changed = new CpuAffinityService().TrySetAffinity(child.Id, child.StartTime.AddSeconds(-1), CoreZero, out var error);

        Assert.False(changed);
        Assert.Equal("That process is no longer running.", error);
        Assert.Equal(before, MaskOf(child));
    }

    [Fact]
    public async Task HasExited_TellsTheListedChildFromAnother_AndSeesItClose()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var service = new CpuAffinityService();
        int id;
        DateTime started;

        using (var child = await RunningChild.StartAsync(heldFile: null, bounded.Token))
        {
            id = child.Id;
            started = child.StartTime;

            Assert.False(service.HasExited(id, started));
            Assert.True(service.HasExited(id, started.AddSeconds(-1)));

            // Closed, while the test still holds a handle to it: Windows keeps an exited process, with its ID and
            // start time, until the last handle to it closes.
            child.Process.StandardInput.Close();
            await child.Process.WaitForExitAsync(bounded.Token);
            Assert.True(service.HasExited(id, started));
        }

        // And with the handle let go: the ID now names nothing, or a process that started later.
        Assert.True(service.HasExited(id, started));
    }

    /// <summary>
    /// The priority Gaming Profile raises a game to, changed only at the game's start time (#2559). Lowered rather than
    /// raised here, since a lower class needs no privilege and the child is the test's own.
    /// </summary>
    [Fact]
    public async Task TrySetPriority_AtTheListedStartTime_ChangesTheChild_AndAtAnotherLeavesItAsItWas()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var child = await RunningChild.StartAsync(heldFile: null, bounded.Token);
        var service = new CpuAffinityService();
        Assert.NotEqual(ProcessPriorityClass.BelowNormal, PriorityOf(child));   // the premise: the change would show

        Assert.False(service.TrySetPriority(child.Id, child.StartTime.AddSeconds(-1), ProcessPriorityClass.BelowNormal,
            out var refused));
        Assert.Equal("That process is no longer running.", refused);
        Assert.NotEqual(ProcessPriorityClass.BelowNormal, PriorityOf(child));

        Assert.True(service.TrySetPriority(child.Id, child.StartTime, ProcessPriorityClass.BelowNormal, out var error), error);
        Assert.Equal(ProcessPriorityClass.BelowNormal, PriorityOf(child));
    }

    private static long MaskOf(RunningChild child)
    {
        child.Process.Refresh();
        return (long)child.Process.ProcessorAffinity;
    }

    private static ProcessPriorityClass PriorityOf(RunningChild child)
    {
        child.Process.Refresh();
        return child.Process.PriorityClass;
    }

    private static void SkipOnASingleCpu()
    {
        if (Environment.ProcessorCount < 2)
            Assert.Skip("Pinning to one core changes nothing on a machine with one logical CPU.");
    }
}
