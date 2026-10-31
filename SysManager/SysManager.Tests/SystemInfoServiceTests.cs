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

    // ---- static-cache fill vs. the dynamic syscalls (#2845) ----

    /// <summary>
    /// A caller that arrives while another is stuck inside the disk query gets its CPU/RAM numbers anyway,
    /// rather than queuing up behind the stall.
    /// </summary>
    /// <remarks>
    /// This is the fault #2845 was filed over: the 300 ms Dashboard loop, the notification-area tooltip and
    /// the Resource History sampler all start by awaiting the same first <c>CaptureAsync</c>, and the static
    /// caches used to be filled under a lock held across all four WMI round trips — so every one of them
    /// waited on whichever provider answered slowest, for numbers (<c>GetSystemTimes</c>,
    /// <c>GlobalMemoryStatusEx</c>) that never touch WMI at all. Here the disk query is made to hang forever;
    /// a second, concurrent capture must still come back with a usable load percentage and RAM total. It
    /// gets the "Unknown"/placeholder static fields for that one pass — the one caller actually doing the
    /// query is still in it — which is the trade this fix makes: deferred, not blocked.
    /// </remarks>
    [Fact]
    public async Task Capture_WhenTheDiskQueryNeverReturns_CpuAndRamStillArriveForAConcurrentCaller()
    {
        var diskQueryStarted = new ManualResetEventSlim(false);
        var diskGate = new ManualResetEventSlim(false);

        var service = new SystemInfoService(
            Times(At(0, 0, 0), At(50, 100, 100), At(50, 100, 100)),
            () => new SystemInfoService.PhysicalMemory(8UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024),
            () => 1234,
            queryOsStatic: () => new OsInfo("Windows 11", "10.0", "22631", TimeSpan.Zero, "64-bit"),
            queryCpuStatic: () => new CpuInfo("Test CPU", 8, 16, 3600, 0),
            queryDisks: () =>
            {
                diskQueryStarted.Set();
                diskGate.Wait(); // released only after the assertions below — simulates a stalled provider
                return [];
            },
            queryMemoryModules: () => []);

        var hungCapture = service.CaptureAsync();
        Assert.True(diskQueryStarted.Wait(TimeSpan.FromSeconds(5)),
            "the first caller never reached the disk query — there is nothing here to prove a concurrent "
            + "caller skips waiting on");

        // Bounded: a caller that joined the lock across the WMI round trip, instead of skipping it, hangs
        // here rather than completing.
        var concurrent = await WithBoundedWait(service.CaptureAsync(), "the concurrent capture");
        Assert.False(double.IsNaN(concurrent.Cpu.LoadPercent));
        Assert.InRange(concurrent.Cpu.LoadPercent, 0, 100);
        Assert.Equal(8.0, concurrent.Memory.TotalGB, precision: 6);
        // The placeholder, not the real query result: the thread that owns the fill hasn't published yet.
        Assert.Equal("Unknown", concurrent.Cpu.Name);

        diskGate.Set();
        var hung = await WithBoundedWait(hungCapture, "the hung capture, after its gate was released");
        Assert.Equal("Test CPU", hung.Cpu.Name); // the owning caller's own query result, not lost
    }

    /// <summary>Concurrent callers that find the fill already claimed do not each start their own.</summary>
    /// <remarks>
    /// Without the claim, every caller that finds a null field during the start-up thundering herd runs its
    /// own four WMI round trips — exactly as many, just no longer serialised behind one lock. The claim turns
    /// that into one fill, found by whichever caller happens to get there first.
    /// </remarks>
    [Fact]
    public async Task Capture_ConcurrentFirstCalls_RunTheDiskQueryOnlyOnce()
    {
        var diskQueryStarted = new ManualResetEventSlim(false);
        var diskGate = new ManualResetEventSlim(false);
        var diskQueryCount = 0;

        var service = new SystemInfoService(
            Times(At(0, 0, 0), At(50, 100, 100), At(50, 100, 100), At(50, 100, 100)),
            () => new SystemInfoService.PhysicalMemory(8UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024),
            () => 1234,
            queryOsStatic: () => new OsInfo("Windows 11", "10.0", "22631", TimeSpan.Zero, "64-bit"),
            queryCpuStatic: () => new CpuInfo("Test CPU", 8, 16, 3600, 0),
            queryDisks: () =>
            {
                Interlocked.Increment(ref diskQueryCount);
                diskQueryStarted.Set();
                diskGate.Wait();
                return [];
            },
            queryMemoryModules: () => []);

        var hungCapture = service.CaptureAsync();
        Assert.True(diskQueryStarted.Wait(TimeSpan.FromSeconds(5)),
            "the first caller never reached the disk query — there is nothing here to prove a second claim "
            + "is refused");

        await WithBoundedWait(service.CaptureAsync(), "a second concurrent capture");
        await WithBoundedWait(service.CaptureAsync(), "a third concurrent capture");

        diskGate.Set();
        await WithBoundedWait(hungCapture, "the hung capture, after its gate was released");

        Assert.Equal(1, diskQueryCount);
    }

    // ---- the disks' health while SysManager runs (#2732) ----

    /// <summary>A clock that stands still until the test moves it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static DiskInfo Disk(string health) => new("Disk 0", "SSD", "NVMe", 512, health, "OK", null, null);

    private static SystemInfoService ServiceOverDisks(Func<List<DiskInfo>?> queryDisks, TimeProvider time) => new(
        Times(At(0, 0, 0)),
        () => new SystemInfoService.PhysicalMemory(8UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024),
        () => 1234,
        queryOsStatic: () => new OsInfo("Windows 11", "10.0", "22631", TimeSpan.Zero, "64-bit"),
        queryCpuStatic: () => new CpuInfo("Test CPU", 8, 16, 3600, 0),
        queryDisks: queryDisks,
        queryMemoryModules: () => [],
        time: time);

    [Fact]
    public async Task ADiskThatTurnsUnhealthy_IsSeen_OnceTheRefreshIntervalHasPassed()
    {
        // The list was read once per session, so the tray's disk alert and System Health never saw the change.
        var health = "Healthy";
        var queries = 0;
        var clock = new ManualClock();
        var service = ServiceOverDisks(() => { queries++; return [Disk(health)]; }, clock);
        Assert.Equal("Healthy", Assert.Single((await service.CaptureAsync()).Disks).HealthStatus);

        health = "Unhealthy";
        clock.Advance(SystemInfoService.DiskRefreshInterval - TimeSpan.FromSeconds(1));
        Assert.Equal("Healthy", Assert.Single((await service.CaptureAsync()).Disks).HealthStatus);
        Assert.Equal(1, queries);   // not on every capture: the vitals poll runs several times a second

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Unhealthy", Assert.Single((await service.CaptureAsync()).Disks).HealthStatus);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task ARefreshThatFaults_KeepsTheDisksAsLastRead_AndWaitsTheIntervalAgain()
    {
        var fault = false;
        var queries = 0;
        var clock = new ManualClock();
        var service = ServiceOverDisks(() => { queries++; return fault ? null : [Disk("Healthy")]; }, clock);
        await service.CaptureAsync();

        fault = true;
        clock.Advance(SystemInfoService.DiskRefreshInterval);
        Assert.Equal("Healthy", Assert.Single((await service.CaptureAsync()).Disks).HealthStatus);
        await service.CaptureAsync();

        Assert.Equal(2, queries);   // the fault is not retried on the very next capture
    }

    /// <summary>A query that faults once is retried on the next call, rather than caching "Unknown" forever.</summary>
    [Fact]
    public async Task Capture_WhenAQueryFaultsOnce_RetriesOnTheNextCall()
    {
        var attempt = 0;
        var service = new SystemInfoService(
            Times(At(0, 0, 0), At(50, 100, 100), At(50, 100, 100)),
            () => new SystemInfoService.PhysicalMemory(8UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024),
            () => 1234,
            queryOsStatic: () => new OsInfo("Windows 11", "10.0", "22631", TimeSpan.Zero, "64-bit"),
            queryCpuStatic: () => ++attempt == 1 ? null : new CpuInfo("Test CPU", 8, 16, 3600, 0),
            queryDisks: () => [],
            queryMemoryModules: () => []);

        var first = await service.CaptureAsync();
        Assert.Equal("Unknown", first.Cpu.Name); // the fault's transient default, not cached

        var second = await service.CaptureAsync();
        Assert.Equal("Test CPU", second.Cpu.Name); // retried rather than stuck on the first fault
    }

    private static async Task<SystemSnapshot> WithBoundedWait(Task<SystemSnapshot> task, string what)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(finished, task),
            $"{what} did not complete within the bound — it is blocked on something it should not be.");
        return await task;
    }
}
