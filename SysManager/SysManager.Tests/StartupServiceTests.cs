// SysManager · StartupServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupService"/>. Verifies that the scanner
/// returns entries from registry, startup folders, and Task Scheduler
/// without crashing on any machine configuration.
/// </summary>
public class StartupServiceTests
{
    [Fact]
    public async Task ScanAsync_ReturnsNonNullList()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ScanAsync_EntriesHaveNonEmptyNames()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name),
                $"Entry with empty name found at location: {entry.Location}");
        }
    }

    [Fact]
    public async Task ScanAsync_EntriesHaveNonEmptyCommand()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Command),
                $"Entry '{entry.Name}' has empty command");
        }
    }

    [Fact]
    public async Task ScanAsync_EntriesHaveValidSource()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.True(Enum.IsDefined(typeof(StartupSource), entry.Source),
                $"Entry '{entry.Name}' has invalid source: {entry.Source}");
        }
    }

    [Fact]
    public async Task ScanAsync_EntriesHaveLocation()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Location),
                $"Entry '{entry.Name}' has empty location");
        }
    }

    [Fact]
    public async Task ScanAsync_NoDuplicateNamesWithinSameSource()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        // Entries from different sources (registry vs folder vs scheduler)
        // may legitimately share a name. Within the same source, entries
        // from Run and RunOnce may also share a name (e.g. "desktop").
        // We check for exact (name + source + location) triples.
        var dupes = result
            .GroupBy(e => (e.Name.ToLowerInvariant(), e.Source, e.Location.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Item1} ({g.Key.Source}, {g.Key.Item3})")
            .ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public async Task ScanAsync_StatusTextIsSet()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.StatusText),
                $"Entry '{entry.Name}' has empty StatusText");
        }
    }

    [Fact]
    public async Task ScanAsync_IsEnabledIsBoolean()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        // Just verify no exceptions — IsEnabled is always bool by type,
        // but we want to ensure ApplyApprovedState doesn't corrupt it.
        foreach (var entry in result)
        {
            _ = entry.IsEnabled; // should not throw
        }
    }
}
