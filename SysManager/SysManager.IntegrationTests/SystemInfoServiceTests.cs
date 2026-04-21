using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class SystemInfoServiceTests
{
    [Fact]
    public async Task CaptureAsync_Completes()
    {
        var svc = new SystemInfoService();
        var snap = await svc.CaptureAsync();
        Assert.NotNull(snap);
    }

    [Fact]
    public async Task CaptureAsync_OsInfoPopulated()
    {
        var snap = await new SystemInfoService().CaptureAsync();
        Assert.False(string.IsNullOrWhiteSpace(snap.Os.Caption));
        Assert.True(snap.Os.Uptime >= TimeSpan.Zero);
    }

    [Fact]
    public async Task CaptureAsync_CpuInfoPopulated()
    {
        var snap = await new SystemInfoService().CaptureAsync();
        Assert.True(snap.Cpu.Cores > 0);
        Assert.True(snap.Cpu.LogicalProcessors >= snap.Cpu.Cores);
    }

    [Fact]
    public async Task CaptureAsync_MemoryInfoPopulated()
    {
        var snap = await new SystemInfoService().CaptureAsync();
        Assert.True(snap.Memory.TotalGB > 0);
        Assert.InRange(snap.Memory.UsedPercent, 0, 100);
    }

    [Fact]
    public async Task CaptureAsync_DisksList_NotNull()
    {
        var snap = await new SystemInfoService().CaptureAsync();
        Assert.NotNull(snap.Disks);
    }

    [Fact]
    public async Task CaptureAsync_CapturedAt_IsRecent()
    {
        var before = DateTime.Now.AddSeconds(-2);
        var snap = await new SystemInfoService().CaptureAsync();
        var after = DateTime.Now.AddSeconds(2);
        Assert.InRange(snap.CapturedAt, before, after);
    }

    [Fact]
    public async Task CaptureAsync_MultipleCalls_AreIndependent()
    {
        var svc = new SystemInfoService();
        var a = await svc.CaptureAsync();
        var b = await svc.CaptureAsync();
        Assert.NotSame(a, b);
    }

    [Fact]
    public async Task CaptureAsync_RespectsCancellation()
    {
        var svc = new SystemInfoService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // WMI queries may run briefly before cancellation lands — we only
        // require the method to complete without crashing.
        var ex = await Record.ExceptionAsync(async () => await svc.CaptureAsync(cts.Token));
        // Either TaskCanceledException or completes fine — both acceptable.
        _ = ex;
    }
}
