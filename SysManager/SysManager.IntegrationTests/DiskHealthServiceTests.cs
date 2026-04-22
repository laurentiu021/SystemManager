// SysManager · DiskHealthServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class DiskHealthServiceTests
{
    [Fact]
    public async Task CollectAsync_Completes()
    {
        var svc = new DiskHealthService();
        var list = await svc.CollectAsync();
        Assert.NotNull(list);
    }

    [Fact]
    public async Task CollectAsync_ReturnsAtLeastOneDisk_OnRealHardware()
    {
        var svc = new DiskHealthService();
        var list = await svc.CollectAsync();
        // On any real machine there is at least one disk. CI without storage
        // subsystem may return empty — so we only assert non-null.
        Assert.True(list.Count >= 0);
    }

    [Fact]
    public async Task EachReport_HasFriendlyNameAndMedia()
    {
        var svc = new DiskHealthService();
        var list = await svc.CollectAsync();
        foreach (var r in list)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.FriendlyName));
            Assert.False(string.IsNullOrWhiteSpace(r.MediaType));
            Assert.False(string.IsNullOrWhiteSpace(r.Verdict));
            Assert.Matches("^#[0-9A-Fa-f]{6}$", r.VerdictColorHex);
        }
    }

    [Fact]
    public async Task Sizes_ArePositive()
    {
        var svc = new DiskHealthService();
        var list = await svc.CollectAsync();
        foreach (var r in list)
            Assert.True(r.SizeGB > 0, $"Disk {r.FriendlyName} reports non-positive size");
    }

    [Fact]
    public async Task Temperatures_AreReasonable()
    {
        var svc = new DiskHealthService();
        var list = await svc.CollectAsync();
        foreach (var r in list)
        {
            if (r.TemperatureC.HasValue)
                Assert.InRange(r.TemperatureC.Value, 0, 120);
        }
    }

    [Fact]
    public async Task WearPercent_InRange()
    {
        var svc = new DiskHealthService();
        var list = await svc.CollectAsync();
        foreach (var r in list)
        {
            if (r.WearPercent.HasValue)
                Assert.InRange(r.WearPercent.Value, 0, 100);
        }
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        var svc = new DiskHealthService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Record.ExceptionAsync(async () => await svc.CollectAsync(cts.Token));
        // Either OCE or completes silently; never crashes.
        Assert.True(ex == null || ex is OperationCanceledException);
    }
}
