// SysManager · SystemInfoServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The three values the Landing tab refreshes every 300 ms. They used to be two WMI round-trips per pass;
/// they are now syscalls behind constructor-injected seams, so the arithmetic — and every way it can be
/// handed nothing usable — is assertable without a real machine.
/// </summary>
public class SystemInfoServiceTests
{
    /// <summary>Feeds successive readings, then repeats the last one, so a test controls each sample.</summary>
    private static Func<SystemInfoService.SystemTimes?> Times(params SystemInfoService.SystemTimes?[] readings)
    {
        var index = 0;
        return () => readings[Math.Min(index++, readings.Length - 1)];
    }

    private static SystemInfoService.SystemTimes At(ulong idle, ulong kernel, ulong user) => new(idle, kernel, user);

    private static SystemInfoService NewService(
        Func<SystemInfoService.SystemTimes?>? times = null,
        Func<SystemInfoService.PhysicalMemory?>? memory = null,
        Func<long>? uptimeMs = null)
        => new(times ?? Times(At(0, 0, 0)),
               memory ?? (() => new SystemInfoService.PhysicalMemory(0, 0)),
               uptimeMs ?? (() => 0));

    // ---- CPU load ----

    /// <summary>
    /// Busy is <c>(kernelΔ + userΔ) - idleΔ</c>, because GetSystemTimes counts idle time inside kernel time.
    /// Here: idle rises 50, kernel 100, user 100 — so 50 of 200 ticks were idle and 75% were busy. Reading it
    /// as "kernel + user are both busy" would report 100%, which is why the row is exact rather than a range.
    /// </summary>
    [Fact]
    public void CpuLoad_IsTheBusyShareOfTheTicksSinceThePreviousSample()
    {
        var service = NewService(Times(At(1000, 2000, 1000), At(1050, 2100, 1100)));
        Assert.Equal(75.0, service.SampleCpuLoad(), precision: 6);
    }

    [Theory]
    // A fully idle interval: every one of the 100 kernel ticks was idle.
    [InlineData(100ul, 100ul, 0ul, 0.0)]
    // A fully busy interval: no idle ticks at all.
    [InlineData(0ul, 100ul, 0ul, 100.0)]
    // Half the interval idle, and user time carrying part of the load.
    [InlineData(100ul, 100ul, 100ul, 50.0)]
    public void CpuLoad_CoversTheEndsOfTheRange(ulong idleDelta, ulong kernelDelta, ulong userDelta, double expected)
    {
        var service = NewService(Times(At(0, 0, 0), At(idleDelta, kernelDelta, userDelta)));
        Assert.Equal(expected, service.SampleCpuLoad(), precision: 6);
    }

    /// <summary>
    /// A failed syscall must not flash 0 into the chart: 0% is a legible reading that means "idle machine",
    /// and the machine's load is simply unknown for this pass.
    /// </summary>
    [Fact]
    public void CpuLoad_WhenTheSyscallFails_HoldsTheLastKnownValue()
    {
        var service = NewService(Times(At(1000, 2000, 1000), At(1050, 2100, 1100), null));
        Assert.Equal(75.0, service.SampleCpuLoad(), precision: 6);
        Assert.Equal(75.0, service.SampleCpuLoad(), precision: 6);
    }

    /// <summary>
    /// With no baseline — the constructor's seeding syscall failed too — the totals since boot ARE a delta
    /// measured from zero, so the since-boot average is the honest answer. Here 250 of 1000 ticks were idle.
    /// </summary>
    [Fact]
    public void CpuLoad_WithNoBaseline_ReportsTheSinceBootAverage()
    {
        var service = NewService(Times(null, At(250, 1000, 0)));
        Assert.Equal(75.0, service.SampleCpuLoad(), precision: 6);
    }

    [Fact]
    public void CpuLoad_WhenTheSyscallNeverSucceeds_ReportsZeroRatherThanNaN()
    {
        var service = NewService(Times(null, null));
        var load = service.SampleCpuLoad();
        Assert.False(double.IsNaN(load));
        Assert.Equal(0.0, load, precision: 6);
    }

    /// <summary>
    /// Two samples inside one timer tick give a zero-tick-wide interval. 0 idle of 0 total is not 0% busy, so
    /// the last real value stands rather than the chart dropping to the floor.
    /// </summary>
    [Fact]
    public void CpuLoad_WhenTwoSamplesLandInTheSameTick_HoldsTheLastValue()
    {
        var service = NewService(Times(At(1000, 2000, 1000), At(1050, 2100, 1100), At(1050, 2100, 1100)));
        Assert.Equal(75.0, service.SampleCpuLoad(), precision: 6);
        Assert.Equal(75.0, service.SampleCpuLoad(), precision: 6);
    }

    /// <summary>
    /// These counters only advance, but a backwards step would wrap the unsigned subtraction into an enormous
    /// delta and a nonsense percentage — so the ordering is checked rather than assumed.
    /// </summary>
    [Fact]
    public void CpuLoad_WhenTheCountersStepBackwards_HoldsTheLastValueInsteadOfWrapping()
    {
        var service = NewService(Times(At(1000, 2000, 1000), At(1050, 2100, 1100), At(900, 1900, 900)));
        Assert.Equal(75.0, service.SampleCpuLoad(), precision: 6);
        Assert.Equal(75.0, service.SampleCpuLoad(), precision: 6);
    }

    /// <summary>Idle can never exceed the total, but if it did the result must stay a percentage.</summary>
    [Fact]
    public void CpuLoad_StaysWithinAPercentageEvenIfIdleExceedsTheTotal()
    {
        var service = NewService(Times(At(0, 0, 0), At(500, 100, 0)));
        var load = service.SampleCpuLoad();
        Assert.InRange(load, 0.0, 100.0);
    }

    /// <summary>Each pass measures the interval just elapsed, not a running average of every interval.</summary>
    [Fact]
    public void CpuLoad_MeasuresEachIntervalIndependently()
    {
        var service = NewService(Times(At(0, 0, 0), At(100, 100, 0), At(100, 200, 0)));
        Assert.Equal(0.0, service.SampleCpuLoad(), precision: 6);
        Assert.Equal(100.0, service.SampleCpuLoad(), precision: 6);
    }

    // ---- memory ----

    [Fact]
    public void Memory_ComesFromTheSyscallAndIsReportedInGigabytes()
    {
        const ulong gib = 1024ul * 1024 * 1024;
        var service = NewService(memory: () => new SystemInfoService.PhysicalMemory(8 * gib, 2 * gib));

        var memory = service.SampleMemory([]);

        Assert.Equal(8.0, memory.TotalGB, precision: 6);
        Assert.Equal(2.0, memory.AvailableGB, precision: 6);
        Assert.Equal(6.0, memory.UsedGB, precision: 6);
        Assert.Equal(75.0, memory.UsedPercent, precision: 6);
    }

    /// <summary>A failed syscall zeroes the totals without dividing by zero, and keeps the DIMM list.</summary>
    [Fact]
    public void Memory_WhenTheSyscallFails_ZeroesTheTotalsAndStillListsTheModules()
    {
        var modules = new List<MemoryModule> { new("DIMM0", "Vendor", 8, 3200, 3200, "PN") };
        var service = NewService(memory: () => null);

        var memory = service.SampleMemory(modules);

        Assert.Equal(0.0, memory.TotalGB, precision: 6);
        Assert.Equal(0.0, memory.AvailableGB, precision: 6);
        Assert.Equal(0.0, memory.UsedPercent, precision: 6);
        Assert.Single(memory.Modules);
    }

    [Fact]
    public void Memory_PassesTheStaticModuleInventoryThrough()
    {
        var modules = new List<MemoryModule>
        {
            new("DIMM0", "Vendor", 8, 3200, 3200, "PN-A"),
            new("DIMM1", "Vendor", 8, 3200, 3200, "PN-B"),
        };
        var service = NewService();

        Assert.Equal(2, service.SampleMemory(modules).Modules.Count);
    }
}
