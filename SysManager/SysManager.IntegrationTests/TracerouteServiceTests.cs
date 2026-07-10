// SysManager · TracerouteServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class TracerouteServiceTests
{
    [Fact]
    public async Task Cancellation_IsHonoredQuickly()
    {
        var svc = new TracerouteService { MaxHops = 30, TimeoutMs = 2000, ProbesPerHop = 2 };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.RunAsync("192.0.2.1", cts.Token));
        sw.Stop();

        // Must return within a reasonable slice of one probe, not the full trace.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6),
            $"Cancellation too slow: {sw.Elapsed}");
    }

    [Fact]
    public async Task InvalidHost_ThrowsInvalidOperation()
    {
        var svc = new TracerouteService { MaxHops = 2, TimeoutMs = 500, ProbesPerHop = 1 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // P2 #39: the destination is now resolved ONCE up front. An unresolvable host
        // fails at that single resolution and surfaces as InvalidOperationException —
        // the type the ViewModel's catch already handles — rather than silently looping
        // MaxHops times with per-probe DNS failures. (Before: the raw hostname went into
        // every SendPingAsync probe, so a bad host produced MaxHops timeout rows.)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RunAsync("this-host-does-not-exist.invalid", cts.Token));
    }

    [Fact]
    public async Task HopCompleted_FiresAtLeastOnceForUnreachableHost()
    {
        var svc = new TracerouteService { MaxHops = 2, TimeoutMs = 500, ProbesPerHop = 1 };
        var hopCount = 0;
        svc.HopCompleted += _ => Interlocked.Increment(ref hopCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hops = await svc.RunAsync("192.0.2.1", cts.Token);
        Assert.Equal(hops.Count, hopCount);
        Assert.True(hopCount >= 1);
    }
}
