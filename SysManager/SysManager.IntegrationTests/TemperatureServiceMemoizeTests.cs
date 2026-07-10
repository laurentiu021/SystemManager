// SysManager · TemperatureServiceMemoizeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Regression (P2 #14): the Dashboard's 2s temperature poll called
/// <c>ReadAllAsync(includeStorage: true)</c>, which re-resolved static disk friendly-names on
/// every tick — a Win32_DiskDrive WMI query plus a DiskHealthService SMART-association walk (the
/// heaviest part of a read). Disk names are static hardware identity, so <see cref="TemperatureService"/>
/// now memoizes both resolutions once. These tests pin the memoization contract at the cache-field
/// level (the service has no interface seam and the resolution paths are elevation/WMI-gated, so a
/// call-count spy isn't available — the private enricher is driven directly and the cache asserted).
/// </summary>
public class TemperatureServiceMemoizeTests
{
    private static readonly MethodInfo EnrichMethod =
        typeof(TemperatureService).GetMethod("EnrichStorageNamesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static FieldInfo CacheField =>
        typeof(TemperatureService).GetField("_cachedStorageFriendlyNames", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static async Task InvokeEnrichAsync(TemperatureService svc, List<TemperatureReading> readings)
        => await (Task)EnrichMethod.Invoke(svc, [readings])!;

    [Fact]
    public async Task EnrichStorageNames_ResolvesOnce_AndCachesFriendlyNames()
    {
        using var svc = new TemperatureService(new DiskHealthService());
        var readings = new List<TemperatureReading>();

        // First enrich resolves + caches the friendly names (real DiskHealthService; on a WMI-less
        // CI host CollectAsync degrades to an empty list — still a valid, cached resolution).
        await InvokeEnrichAsync(svc, readings);
        var afterFirst = CacheField.GetValue(svc);
        Assert.NotNull(afterFirst); // cache populated (even if empty) — never re-resolved after this

        // Second enrich must reuse the SAME cached instance, not re-run the SMART walk.
        await InvokeEnrichAsync(svc, readings);
        var afterSecond = CacheField.GetValue(svc);
        Assert.Same(afterFirst, afterSecond);
    }
}
