// SysManager · PingMonitorStressTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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

        await churn;
        svc.Stop();

        // If we got here without an exception, the concurrent access is safe.
        Assert.True(true);
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
