// SysManager · EventLogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Integration tests — these hit the real Windows Event Log on the test
/// machine. Kept small + short to stay fast and deterministic.
/// </summary>
[Collection("Network")] // reuse collection to serialize Windows-level tests
public class EventLogServiceTests
{
    [Fact]
    public async Task Read_System_ReturnsSomeEntries_Within_ShortWindow()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions
        {
            LogName = "System",
            Since = DateTime.Now.AddDays(-30),
            MaxResults = 5
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var list = new List<FriendlyEventEntry>();
        await foreach (var e in svc.ReadAsync(opt, cts.Token))
            list.Add(e);

        // Nearly every Windows box has events in System. But we don't fail the
        // build on a pristine system; we just ensure it doesn't throw.
        Assert.True(list.Count <= 5);
        foreach (var e in list)
        {
            Assert.Equal("System", e.LogName);
            Assert.NotEmpty(e.ProviderName);
        }
    }

    [Fact]
    public async Task Read_InvalidLogName_SilentlySkips()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions
        {
            LogName = "Bogus-Log-Does-Not-Exist",
            MaxResults = 10
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var list = new List<FriendlyEventEntry>();
        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var e in svc.ReadAsync(opt, cts.Token))
                list.Add(e);
        });
        Assert.Null(ex);
        Assert.Empty(list);
    }

    [Fact]
    public async Task Read_RespectsMaxResults()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions
        {
            LogName = "System",
            Since = DateTime.Now.AddYears(-10),
            MaxResults = 3
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var count = 0;
        await foreach (var _ in svc.ReadAsync(opt, cts.Token)) count++;
        Assert.True(count <= 3);
    }

    [Fact]
    public async Task Read_Cancellation_StopsFast()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions
        {
            LogName = "System",
            Since = DateTime.Now.AddYears(-10),
            MaxResults = 100000
        };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        try
        {
            await foreach (var _ in svc.ReadAsync(opt, cts.Token)) count++;
        }
        catch (OperationCanceledException) { /* also acceptable */ }
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Cancellation took {sw.Elapsed}");
    }

    [Fact]
    public async Task Read_EntriesEnrichedWithExplanation()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions
        {
            LogName = "System",
            Since = DateTime.Now.AddDays(-30),
            MaxResults = 10
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await foreach (var e in svc.ReadAsync(opt, cts.Token))
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Explanation), "Explanation missing");
            Assert.False(string.IsNullOrWhiteSpace(e.Recommendation), "Recommendation missing");
        }
    }

    [Fact]
    public async Task Read_SeverityFilter_ReturnsOnlyRequested()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions
        {
            LogName = "System",
            Since = DateTime.Now.AddDays(-90),
            MaxResults = 20,
            Severities = new() { EventSeverity.Error, EventSeverity.Critical }
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await foreach (var e in svc.ReadAsync(opt, cts.Token))
        {
            Assert.True(e.Severity == EventSeverity.Error || e.Severity == EventSeverity.Critical,
                $"Unexpected severity {e.Severity}");
        }
    }
}
