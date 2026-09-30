// SysManager · CpuAffinityServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using SysManager.Services;

namespace SysManager.Tests;

public class CpuAffinityServiceTests
{
    [Theory]
    [InlineData(1, 0b1L)]
    [InlineData(2, 0b11L)]
    [InlineData(4, 0b1111L)]
    [InlineData(8, 0xFFL)]
    public void AllCoresMask_SetsLowBits(int count, long expected)
        => Assert.Equal(expected, CpuAffinityService.AllCoresMask(count));

    [Fact]
    public void AllCoresMask_64_IsAllBits()
        => Assert.Equal(-1L, CpuAffinityService.AllCoresMask(64));

    [Fact]
    public void MaskFromIndices_BuildsBitmask()
    {
        Assert.Equal(0b1011L, CpuAffinityService.MaskFromIndices([0, 1, 3]));
        Assert.Equal(0L, CpuAffinityService.MaskFromIndices([]));
    }

    [Fact]
    public void MaskFromIndices_IgnoresOutOfRangeIndices()
    {
        // Negative and >=64 are dropped, not crash.
        Assert.Equal(0b1L, CpuAffinityService.MaskFromIndices([0, -1, 64, 99]));
    }

    [Theory]
    [InlineData(0b1010L, 1, true)]
    [InlineData(0b1010L, 0, false)]
    [InlineData(0b1010L, 3, true)]
    [InlineData(0b1010L, 2, false)]
    public void IsCoreInMask_ChecksBit(long mask, int index, bool expected)
        => Assert.Equal(expected, CpuAffinityService.IsCoreInMask(mask, index));

    [Fact]
    public void IsCoreInMask_OutOfRange_IsFalse()
    {
        Assert.False(CpuAffinityService.IsCoreInMask(-1L, -1));
        Assert.False(CpuAffinityService.IsCoreInMask(-1L, 64));
    }

    [Fact]
    public void RoundTrip_IndicesToMaskToCheck()
    {
        long mask = CpuAffinityService.MaskFromIndices([2, 5, 7]);
        Assert.True(CpuAffinityService.IsCoreInMask(mask, 2));
        Assert.True(CpuAffinityService.IsCoreInMask(mask, 5));
        Assert.True(CpuAffinityService.IsCoreInMask(mask, 7));
        Assert.False(CpuAffinityService.IsCoreInMask(mask, 3));
    }

    // ── Which process an ID names (#2514) ──
    // Windows gives a closed process's ID to the next one started, so the tab's list can name a process by an ID that
    // now belongs to another. Driven on this test process, with its own start time and with one a second off, which is
    // what a reused ID looks like from the list's side. Nothing here changes the process: the one write passes the
    // mask it already has, so even a broken check would leave it as it was. The integration suite changes a child.

    private static (int Id, DateTime StartTime, long Mask) Self()
    {
        using var self = Process.GetCurrentProcess();
        return (self.Id, self.StartTime, (long)self.ProcessorAffinity);
    }

    [Fact]
    public void GetProcesses_ListsThisProcessWithItsStartTime()
    {
        var (id, startTime, _) = Self();

        var listed = Assert.Single(new CpuAffinityService().GetProcesses(), p => p.ProcessId == id);

        Assert.Equal(startTime, listed.StartTime);
    }

    [Fact]
    public void GetAffinity_AtTheListedStartTime_ReadsTheMask()
    {
        var (id, startTime, mask) = Self();

        Assert.Equal(mask, new CpuAffinityService().GetAffinity(id, startTime));
    }

    [Fact]
    public void GetAffinity_ForAProcessThatStartedAtAnotherTime_IsNull()
    {
        var (id, startTime, _) = Self();

        Assert.Null(new CpuAffinityService().GetAffinity(id, startTime.AddSeconds(-1)));
    }

    [Fact]
    public void TrySetAffinity_ForAProcessThatStartedAtAnotherTime_ChangesNothing()
    {
        var (id, startTime, mask) = Self();

        var changed = new CpuAffinityService().TrySetAffinity(id, startTime.AddSeconds(-1), mask, out var error);

        Assert.False(changed);
        Assert.Equal("That process is no longer running.", error);
    }

    [Fact]
    public void HasExited_ForThisProcessAtItsStartTime_IsFalse()
    {
        var (id, startTime, _) = Self();

        Assert.False(new CpuAffinityService().HasExited(id, startTime));
    }

    [Fact]
    public void HasExited_ForAProcessThatStartedAtAnotherTime_IsTrue()
    {
        var (id, startTime, _) = Self();

        Assert.True(new CpuAffinityService().HasExited(id, startTime.AddSeconds(-1)));
    }

    [Fact]
    public void HasExited_WithNoStartTimeToCheck_AsksOnlyWhetherTheIdIsRunning()
    {
        // int.MaxValue names no process; a smaller made-up ID could, since Windows ignores an ID's low two bits.
        var service = new CpuAffinityService();

        Assert.False(service.HasExited(Environment.ProcessId, null));
        Assert.True(service.HasExited(int.MaxValue, null));
    }

    // ── The same check for the priority Gaming Profile raises (#2559) ──
    // The one write passes the priority this process already has, for the reason the affinity write above does.

    private static (int Id, DateTime StartTime, ProcessPriorityClass Priority) SelfPriority()
    {
        using var self = Process.GetCurrentProcess();
        return (self.Id, self.StartTime, self.PriorityClass);
    }

    [Fact]
    public void GetPriority_AtTheListedStartTime_ReadsTheClass()
    {
        var (id, startTime, priority) = SelfPriority();

        Assert.Equal(priority, new CpuAffinityService().GetPriority(id, startTime));
    }

    [Fact]
    public void GetPriority_ForAProcessThatStartedAtAnotherTime_IsNull()
    {
        var (id, startTime, _) = SelfPriority();

        Assert.Null(new CpuAffinityService().GetPriority(id, startTime.AddSeconds(-1)));
    }

    [Fact]
    public void TrySetPriority_ForAProcessThatStartedAtAnotherTime_ChangesNothing()
    {
        var (id, startTime, priority) = SelfPriority();

        var changed = new CpuAffinityService().TrySetPriority(id, startTime.AddSeconds(-1), priority, out var error);

        Assert.False(changed);
        Assert.Equal("That process is no longer running.", error);
    }
}
