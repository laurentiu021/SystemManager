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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // The churn deliberately never touches "base" — only 192.0.2.2-253 hosts — so
        // it survives as a fixed point we can assert on after the storm settles.
        var churn = Task.Run(() =>
        {
            var rnd = new Random(42);
            while (!cts.IsCancellationRequested)
            {
                var h = $"192.0.2.{rnd.Next(2, 254)}";
                svc.AddOrUpdate(new PingTarget("x", h, "#111"));
                if (rnd.Next(2) == 0) svc.Remove(h);
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
