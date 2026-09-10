// SysManager · PingMonitorStressTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class PingMonitorStressTests
{
    private const string Unreachable = "192.0.2.1";

    [Fact]
    public async Task ManyTargets_NoExceptions()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(200),
            TimeoutMs = 500
        };

        for (int i = 1; i <= 25; i++)
            svc.AddOrUpdate(new PingTarget($"T{i}", $"192.0.2.{i}", "#111"));

        var exceptions = new List<Exception>();
        svc.SampleReceived += _ => { /* just drain */ };

        try
        {
            svc.Start();
            await Task.Delay(1200);
        }
        catch (Exception ex) { exceptions.Add(ex); }
        finally { svc.Stop(); }

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ParallelAddRemoveWhileRunning_IsThreadSafe()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(120),
            TimeoutMs = 400
        };
        svc.AddOrUpdate(new PingTarget("base", Unreachable, "#111"));
        svc.Start();
        Assert.True(svc.IsRunning);

        // The churn deliberately never touches "base" — only 192.0.2.2-253 hosts — so
        // it survives as a fixed point we can assert on after the storm settles. "base"
        // stays enabled, so the pump is genuinely pinging while the map churns under it.
        //
        // Two things here are load-bearing rather than tidiness, and both were measured
        // on #2195. This test used to spin unthrottled for two seconds against a
        // CancellationTokenSource, with the churned targets left ENABLED.
        //
        // ENABLED was the expensive half. PumpAsync snapshots
        // `Targets.Values.Where(t => t.IsEnabled …)` every tick, so a disabled target is
        // still enumerated — the concurrent add/remove still races that snapshot, which
        // is the entire point of this test — but it is never pinged. With them enabled the
        // map filled with ~250 hosts and the pump fired thousands of fire-and-forget ICMP
        // operations at unroutable addresses, all abandoned mid-flight when Stop()
        // cancelled the token. The suite runs maxParallelThreads=1, so those continuations
        // drained through one worker AFTER this test returned, and the bill landed on
        // whichever test ran next: ToggleIsEnabled_MidFlight_IsRespected measured 283s
        // against 1.2s of intended Task.Delay, and 336s on a slow run. Past 300s it
        // crossed --hangdump-timeout 5m, the runner wrote a 704 MB dump, and the hang-dump
        // extension's DisposeAsync then timed out and reported `Failed!` with `failed: 0`
        // on 8 of 60 runs. Disabling them moved that test to 7.64s.
        //
        // UNTHROTTLED was the unstable half, and disabling the targets alone did not fix
        // it — it relocated it. The two-second spin measured ~27 MILLION add/remove
        // operations, i.e. ~14 million ConcurrentDictionary writes a second alongside a
        // pump snapshotting the same dictionary every tick, and the cost of that was never
        // stable: the same runner image measured this pair at 4s + 283s, then at 217s + 8s.
        // Interleaving is what proves thread safety, not iteration count, so the churn is
        // bounded on OPERATIONS and yields between batches — a fixed ~20k operations
        // spread across several pump ticks, at a cost that does not depend on how fast the
        // machine happens to be.
        //
        // Yes, that is an awaited delay in a test, which ArchitectureTests.NoTestWaitsBySleeping
        // forbids — and that guard is scoped to the blocking unit suite, deliberately, not to
        // this project. The rule exists because a unit test must not assert how fast the
        // machine is. Here the subject under test IS a timer: PumpAsync ticks on Interval, so
        // the churn has to span ticks to race the snapshot at all, and Task.Yield() would
        // finish all 20k operations before the pump ticked once. The delay is what makes this
        // test's cost machine-INdependent rather than dependent on it.
        const int ChurnOperations = 20_000;
        var churn = Task.Run(async () =>
        {
            var rnd = new Random(42);
            for (int i = 0; i < ChurnOperations; i++)
            {
                var h = $"192.0.2.{rnd.Next(2, 254)}";
                svc.AddOrUpdate(new PingTarget("x", h, "#111") { IsEnabled = false });
                if (rnd.Next(2) == 0) svc.Remove(h);
                if (i % 1_000 == 0) await Task.Delay(25);
            }
        });

        // A concurrency fault inside the loop faults this Task, so awaiting it here
        // rethrows and fails the test — no separate exception plumbing needed.
        await churn;
        svc.Stop();

        // Concrete post-conditions after concurrent add/remove: the service stopped
        // cleanly, its target map is still coherent, and the untouched "base" target
        // is intact (a corrupt ConcurrentDictionary would drop or duplicate it).
        Assert.False(svc.IsRunning);
        Assert.True(svc.Targets.ContainsKey(Unreachable));
        Assert.Equal("base", svc.Targets[Unreachable].Name);
        Assert.All(svc.Targets.Keys, Assert.NotNull);
    }

    [Fact]
    public async Task ManyStartStopCycles_NoLeak()
    {
        for (int i = 0; i < 10; i++)
        {
            using var svc = new PingMonitorService
            {
                Interval = TimeSpan.FromMilliseconds(50),
                TimeoutMs = 200
            };
            svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111"));
            svc.Start();
            await Task.Delay(80);
            svc.Stop();
            Assert.False(svc.IsRunning);
        }
    }

    [Fact]
    public async Task ToggleIsEnabled_MidFlight_IsRespected()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 400
        };
        var target = new PingTarget("x", Unreachable, "#111");
        svc.AddOrUpdate(target);

        long count = 0;
        svc.SampleReceived += _ => Interlocked.Increment(ref count);
        svc.Start();
        await Task.Delay(400);

        target.IsEnabled = false;
        await Task.Delay(100); // allow any in-flight to resolve
        var before = Interlocked.Read(ref count);

        await Task.Delay(700);
        var after = Interlocked.Read(ref count);
        svc.Stop();

        // After disabling, no new samples should be observed.
        Assert.Equal(before, after);
    }
}
