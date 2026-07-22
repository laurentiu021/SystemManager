// SysManager · BandwidthAggregationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Linq;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ConnectionBandwidthSource.AggregateConnections"/> — folding a flat list of
/// PID→connection rows into one per-process row with a connection count and port summary. Pure, so
/// it runs without the native TCP/UDP tables.
/// </summary>
public class BandwidthAggregationTests
{
    private static ConnectionBandwidthSource.ConnectionRow Row(int pid, string name, int port, bool tcp = true)
        => new(pid, name, port, tcp);

    [Fact]
    public void Aggregate_GroupsByPid_AndCountsConnections()
    {
        var rows = new[]
        {
            Row(100, "chrome.exe", 443),
            Row(100, "chrome.exe", 443),
            Row(100, "chrome.exe", 80),
            Row(200, "spotify.exe", 4070),
        };

        var result = ConnectionBandwidthSource.AggregateConnections(rows);

        Assert.Equal(2, result.Count);
        var chrome = result.First(r => r.ProcessId == 100);
        Assert.Equal(3, chrome.ConnectionCount);
        Assert.Equal("chrome.exe", chrome.ProcessName);
        Assert.True(chrome.IsActive);
        Assert.Contains("443", chrome.RemoteSummary);
    }

    [Fact]
    public void Aggregate_OrdersByConnectionCountDescending()
    {
        var rows = new[]
        {
            Row(1, "a.exe", 1),
            Row(2, "b.exe", 1),
            Row(2, "b.exe", 2),
            Row(2, "b.exe", 3),
        };

        var result = ConnectionBandwidthSource.AggregateConnections(rows);

        Assert.Equal(2, result[0].ProcessId); // b.exe has more connections, sorts first
        Assert.Equal(1, result[1].ProcessId);
    }

    [Fact]
    public void Aggregate_SkipsPidZero()
    {
        // PID 0 (System Idle / unattributable) must never appear as a row.
        var result = ConnectionBandwidthSource.AggregateConnections([Row(0, "", 443), Row(5, "x.exe", 80)]);
        Assert.Single(result);
        Assert.Equal(5, result[0].ProcessId);
    }

    [Fact]
    public void Aggregate_FallsBackToPidLabel_WhenNameMissing()
    {
        var result = ConnectionBandwidthSource.AggregateConnections([Row(42, "", 443)]);
        Assert.Equal("PID 42", result[0].ProcessName);
    }

    [Fact]
    public void Aggregate_Empty_ReturnsEmpty()
        => Assert.Empty(ConnectionBandwidthSource.AggregateConnections([]));

    /// <summary>
    /// A test double that injects a fixed connection set, proving <see cref="ConnectionBandwidthSource"/>
    /// turns enumerated connections into per-process rows via <c>SampleAsync</c> without the native
    /// tables. The interface-total rates come back as real (likely zero on the test agent) values —
    /// we assert on the process attribution, which is what this source uniquely provides.
    /// </summary>
    private sealed class FakeConnectionSource(IReadOnlyList<ConnectionBandwidthSource.ConnectionRow> rows) : ConnectionBandwidthSource
    {
        protected override IReadOnlyList<ConnectionBandwidthSource.ConnectionRow> EnumerateConnections() => rows;
    }

    [Fact]
    public async Task SampleAsync_ProjectsInjectedConnectionsIntoProcessRows()
    {
        var src = new FakeConnectionSource(
        [
            new(100, "chrome.exe", 443, true),
            new(100, "chrome.exe", 443, true),
            new(200, "game.exe", 27015, false),
        ]);
        src.Start();

        var snap = await src.SampleAsync();

        Assert.Equal(BandwidthMode.Connections, snap.Mode);
        Assert.Equal(2, snap.Processes.Count);
        Assert.Equal(100, snap.Processes[0].ProcessId);       // chrome has 2 connections → first
        Assert.Equal(2, snap.Processes[0].ConnectionCount);
    }
}
