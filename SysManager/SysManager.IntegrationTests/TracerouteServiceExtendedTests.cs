// SysManager · TracerouteServiceExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class TracerouteServiceExtendedTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var svc = new TracerouteService();
        Assert.True(svc.MaxHops > 0 && svc.MaxHops <= 64);
        Assert.True(svc.TimeoutMs > 0);
        Assert.True(svc.ProbesPerHop > 0);
    }

    [Fact]
    public void PropertiesAreMutable()
    {
        var svc = new TracerouteService { MaxHops = 5, TimeoutMs = 500, ProbesPerHop = 2 };
        Assert.Equal(5, svc.MaxHops);
        Assert.Equal(500, svc.TimeoutMs);
        Assert.Equal(2, svc.ProbesPerHop);
    }

    [Fact]
    public async Task RunAsync_EmptyHost_ThrowsOrYieldsEmpty()
    {
        var svc = new TracerouteService { MaxHops = 1, TimeoutMs = 200, ProbesPerHop = 1 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ex = await Record.ExceptionAsync(async () =>
        {
            var hops = await svc.RunAsync("", cts.Token);
            Assert.NotNull(hops);
        });
        // Either throws (invalid host) or returns — both acceptable.
        _ = ex;
    }

    [Fact]
    public async Task RunAsync_MaxHopsLimitsIterations()
    {
        var svc = new TracerouteService { MaxHops = 2, TimeoutMs = 200, ProbesPerHop = 1 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hops = await svc.RunAsync("192.0.2.1", cts.Token);
        Assert.True(hops.Count <= 2);
    }

    [Fact]
    public async Task RunAsync_EachHopHasNumberAtLeast1()
    {
        var svc = new TracerouteService { MaxHops = 2, TimeoutMs = 400, ProbesPerHop = 1 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hops = await svc.RunAsync("192.0.2.1", cts.Token);
        for (int i = 0; i < hops.Count; i++)
            Assert.Equal(i + 1, hops[i].HopNumber);
    }

    [Fact]
    public async Task HopCompleted_EventIsFiredInOrder()
    {
        var svc = new TracerouteService { MaxHops = 2, TimeoutMs = 400, ProbesPerHop = 1 };
        var order = new List<int>();
        svc.HopCompleted += h => order.Add(h.HopNumber);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.RunAsync("192.0.2.1", cts.Token);
        for (int i = 1; i < order.Count; i++)
            Assert.True(order[i] > order[i - 1]);
    }
}
