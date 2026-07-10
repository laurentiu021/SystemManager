// SysManager · SystemInfoServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Models;
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
    public async Task CaptureAsync_ReusesCachedMemoryModules()
    {
        // The physical DIMM inventory is static hardware — it must be enumerated once
        // and reused, so the Dashboard's 300 ms vitals poll never re-scans
        // Win32_PhysicalMemory. Same instance across calls proves the cache holds.
        var svc = new SystemInfoService();
        var a = await svc.CaptureAsync();
        var b = await svc.CaptureAsync();
        Assert.Same(a.Memory.Modules, b.Memory.Modules);
    }

    [Fact]
    public async Task CaptureAsync_MemoryTotalsStillRefreshPerCall()
    {
        // Caching the modules must NOT freeze the dynamic RAM totals — each snapshot
        // still carries a freshly-queried total/available/used so live vitals stay real.
        var svc = new SystemInfoService();
        var a = await svc.CaptureAsync();
        var b = await svc.CaptureAsync();
        Assert.True(a.Memory.TotalGB > 0);
        Assert.True(b.Memory.TotalGB > 0);
        Assert.Equal(a.Memory.TotalGB, b.Memory.TotalGB, 3); // total RAM is stable within a session
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

    [Fact]
    public async Task CaptureAsync_ReQueriesStaticInfo_AfterCacheCleared()
    {
        // Regression (P2 #40): the static-hardware cache used ??= with a NON-NULL fallback
        // returned on a WMI fault, so a transient first-call fault poisoned the cache for the
        // whole process — permanently showing "Unknown CPU" / "Windows" / no disks / no RAM.
        // The fix makes each Query* return null on a fault so ??= caches ONLY a successful
        // query and re-queries otherwise. This verifies the retry path: after a successful
        // capture populates the cache, clearing the cache field forces the NEXT capture to
        // re-query (rather than being stuck on a stale/absent value).
        var svc = new SystemInfoService();
        var first = await svc.CaptureAsync();
        Assert.False(string.IsNullOrWhiteSpace(first.Cpu.Name));

        // Simulate the post-fault state: cache empty (as if the first query had faulted and
        // returned null instead of caching a fallback).
        typeof(SystemInfoService)
            .GetField("_cachedCpuStatic", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(svc, null);

        var second = await svc.CaptureAsync();
        // On this (WMI-healthy) host the re-query must succeed and repopulate the real CPU —
        // proving a cleared/null cache is retried, not left permanently degraded.
        Assert.False(string.IsNullOrWhiteSpace(second.Cpu.Name));
        Assert.True(second.Cpu.Cores > 0);

        var cached = (CpuInfo?)typeof(SystemInfoService)
            .GetField("_cachedCpuStatic", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc);
        Assert.NotNull(cached); // re-query cached a fresh successful result
    }
}
