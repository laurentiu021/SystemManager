// SysManager · NetworkSharedStateTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class NetworkSharedStateTests
{
    [Fact]
    public void Constructor_SeedsGatewayAndPreset()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        Assert.NotEmpty(state.Targets);
    }

    [Fact]
    public void AddTarget_IgnoresDuplicate()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var before = state.Targets.Count;
        state.AddTarget("Dup", state.Targets[0].Host);
        Assert.Equal(before, state.Targets.Count);
    }

    [Fact]
    public void AddCustomTarget_EmptyHost_DoesNothing()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        state.NewTargetHost = "";
        var before = state.Targets.Count;
        state.AddCustomTarget();
        Assert.Equal(before, state.Targets.Count);
    }

    [Fact]
    public void AddCustomTarget_ValidHost_AddsTarget()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        state.NewTargetHost = "10.99.99.99";
        var before = state.Targets.Count;
        state.AddCustomTarget();
        Assert.Equal(before + 1, state.Targets.Count);
        Assert.Equal("", state.NewTargetHost);
    }

    [Fact]
    public void RemoveTarget_NonCustom_DoesNothing()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var first = state.Targets.FirstOrDefault(t => !t.IsCustom);
        // Asserted rather than returned on: the constructor ends with ApplyPreset(TargetPresets.Global), and
        // AddTarget defaults isCustom to false, so a non-custom target always exists to try removing.
        Assert.NotNull(first);
        var before = state.Targets.Count;
        state.RemoveTarget(first);
        Assert.Equal(before, state.Targets.Count);
    }

    [Fact]
    public void RemoveTarget_Null_DoesNothing()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var before = state.Targets.Count;
        state.RemoveTarget(null);
        Assert.Equal(before, state.Targets.Count);
    }

    [Fact]
    public void ClearHistory_ResetsAllTargetStats()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        state.ClearHistory();
        Assert.All(state.Targets, t =>
        {
            Assert.Null(t.LastLatencyMs);
            Assert.Null(t.AverageMs);
            Assert.Null(t.JitterMs);
            Assert.Equal(0, t.LossPercent);
            Assert.Equal("—", t.Status);
        });
    }

    [Fact]
    public void ApplyPreset_SwitchesTargets()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        state.ApplyPreset(TargetPresets.All[0]);
        Assert.True(state.Targets.Count > 0);
    }

    public static TheoryData<string> PresetNames()
    {
        var data = new TheoryData<string>();
        foreach (var preset in TargetPresets.All) data.Add(preset.Name);
        return data;
    }

    /// <summary>
    /// Every preset leaves a public DNS server among the targets, so the verdict can tell a far end's loss from the
    /// provider's (#2712).
    /// </summary>
    /// <remarks>
    /// The game and streaming presets pinged none, and the verdict said the connection to DNS was clean. Nothing is
    /// pinged here: the monitor is never started.
    /// </remarks>
    [Theory]
    [MemberData(nameof(PresetNames))]
    public void EveryPreset_PingsAPublicDnsServer(string preset)
    {
        using var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(),
            new Services.TracerouteMonitorService(), new Services.SpeedTestService(),
            new Services.NetworkRepairService(new Services.PowerShellRunner()));

        state.ApplyPreset(TargetPresets.All.Single(p => p.Name == preset));

        Assert.Contains(state.Targets, t => t.Role == TargetRole.PublicDns);
    }

    /// <summary>
    /// A target added on a light theme draws its lines, and its card's bar, in the colour that reads on that theme's
    /// card (#2818).
    /// </summary>
    /// <remarks>
    /// The chart theme is applied when the state is built, before any target exists, so the lines added after it
    /// kept the dark palette on a light theme until the theme next changed. The hosts are documentation addresses,
    /// and nothing is pinged: the monitor is never started.
    /// </remarks>
    [Fact]
    public void ATargetAddedOnALightTheme_IsDrawnInTheColourThatReadsOnIt()
    {
        var light = Services.ThemePreset.Defaults["clean-indigo"];
        var card = SysManager.Helpers.ChartTheme.Sk(light.Surface);
        using var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(),
            new Services.TracerouteMonitorService(), new Services.SpeedTestService(),
            new Services.NetworkRepairService(new Services.PowerShellRunner()))
        {
            Theme = () => light,
        };

        var moved = 0;
        for (var i = 1; i <= NetworkSharedState.Palette.Length; i++)
        {
            var host = $"203.0.113.{i}";
            state.AddTarget($"Edge {i}", host, isCustom: true);
            var target = state.Targets.Single(t => t.Host == host);
            var designed = SKColor.Parse(target.ColorHex.TrimStart('#')).WithAlpha(230);
            var expected = SysManager.Helpers.ChartTheme.ReadableAgainst(designed, card);

            var latency = LatencyLine(state, host);
            var trace = TraceLine(state, host);
            Assert.Equal(expected, Paint(latency.Stroke));
            Assert.Equal(expected, Paint(latency.GeometryStroke));
            Assert.Equal(expected, Paint(latency.GeometryFill));
            Assert.Equal(expected, Paint(trace.Stroke));
            Assert.Equal(expected, Paint(trace.GeometryStroke));
            Assert.Equal($"#{expected.Red:X2}{expected.Green:X2}{expected.Blue:X2}", target.ShownColorHex);
            if (expected != designed) moved++;
        }

        // On Clean Indigo's card most of the palette has to move; one that moved none would prove nothing.
        Assert.True(moved >= 4, $"only {moved} of the {NetworkSharedState.Palette.Length} colours moved");
    }

    private static LineSeries<DateTimePoint> LatencyLine(NetworkSharedState state, string host) =>
        state.LatencySeries.OfType<LineSeries<DateTimePoint>>()
            .Single(s => s.Name!.EndsWith($"({host})", StringComparison.Ordinal));

    private static LineSeries<ObservablePoint> TraceLine(NetworkSharedState state, string host) =>
        state.TraceSeries.OfType<LineSeries<ObservablePoint>>()
            .Single(s => s.Name!.EndsWith($"({host})", StringComparison.Ordinal));

    private static SKColor Paint(object? paint) => Assert.IsType<SolidColorPaint>(paint).Color;

    [Fact]
    public void Health_IsNotNull()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        Assert.NotNull(state.Health);
    }

    [Fact]
    public void LatencySeries_MatchesTargetCount()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        Assert.Equal(state.Targets.Count, state.LatencySeries.Count);
    }

    [Fact]
    public void TraceSeries_MatchesTargetCount()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        Assert.Equal(state.Targets.Count, state.TraceSeries.Count);
    }

    [Fact]
    public void Presets_NotEmpty()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        Assert.NotEmpty(state.Presets);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        Assert.Equal(1, state.IntervalSeconds);
        Assert.Equal(60, state.WindowSeconds);
        Assert.Equal(60, state.TraceIntervalSeconds);
        Assert.False(state.IsMonitoring);
    }

    [Fact]
    public void TrimBuffer_RemovesExpiredPoints()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var buffer = new SysManager.Helpers.BulkObservableCollection<LiveChartsCore.Defaults.DateTimePoint>
        {
            new(DateTime.Now.AddSeconds(-120), 10),
            new(DateTime.Now.AddSeconds(-90), 15),
            new(DateTime.Now.AddSeconds(-30), 20),
            new(DateTime.Now.AddSeconds(-5), 25),
        };
        // WindowSeconds defaults to 60, so first two points should be trimmed
        state.TrimBuffer(buffer);
        Assert.Equal(2, buffer.Count);
        Assert.Equal(20, buffer[0].Value);
        Assert.Equal(25, buffer[1].Value);
    }

    [Fact]
    public void FlushPending_PinsXAxisLimits()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        var host = state.Targets[0].Host;
        state.Pending.Enqueue(new Models.PingSample(DateTime.UtcNow, host, 15.0, "OK"));
        state.FlushPending();

        // After flushing with data, X-axis should have MinLimit and MaxLimit set
        Assert.NotNull(state.LatencyXAxes[0].MinLimit);
        Assert.NotNull(state.LatencyXAxes[0].MaxLimit);
    }

    [Fact]
    public void ClearHistory_ResetsAxisLimits()
    {
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));
        // Simulate pinned axes
        state.LatencyXAxes[0].MinLimit = DateTime.Now.AddSeconds(-60).Ticks;
        state.LatencyXAxes[0].MaxLimit = DateTime.Now.Ticks;

        state.ClearHistory();

        Assert.Null(state.LatencyXAxes[0].MinLimit);
        Assert.Null(state.LatencyXAxes[0].MaxLimit);
    }

    [Fact]
    public void StableOffset_IsDeterministicForSameHost()
    {
        Assert.Equal(NetworkSharedState.StableOffset("example.com"),
                     NetworkSharedState.StableOffset("example.com"));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("example.com")]
    [InlineData("")]
    public void StableOffset_StaysWithinExpectedRange(string host)
    {
        // stableIdx in [0,7] -> ((idx)-3.5)*0.25 spans [-0.875, 0.875].
        var offset = NetworkSharedState.StableOffset(host);
        Assert.InRange(offset, -0.875, 0.875);
    }

    [Fact]
    public void StableOffset_DoesNotThrowOnMinValueHash()
    {
        // Regression: a host whose GetHashCode() is int.MinValue must not throw.
        // The previous Math.Abs(int.MinValue) would have thrown OverflowException;
        // bit-masking with int.MaxValue keeps it safe. Sweep many strings as a proxy
        // for hitting negative/extreme hashes, and assert none throw.
        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < 5000; i++)
                _ = NetworkSharedState.StableOffset("host-" + i);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Regression: Dispose runs more than once. The window's shutdown disposes this through
        // MainWindowViewModel, and the provider disposes the singleton again on teardown.
        // Without the _disposed guard, the second pass double-freed the unmanaged SkiaSharp
        // paint/typeface handles — undefined behavior. The guard must make it a no-op.
        var state = new NetworkSharedState(new Services.PingMonitorService(), new Services.TracerouteService(), new Services.TracerouteMonitorService(), new Services.SpeedTestService(), new Services.NetworkRepairService(new Services.PowerShellRunner()));

        state.Dispose();
        var ex = Record.Exception(() => state.Dispose());
        Assert.Null(ex);
    }

    // ---------- the route of a host that is not a Ping target, and the auto-trace host (#2870) ----------
    // The Hops table and the chart showed Ping targets only, so a trace of any other host ended "Done" with nothing on
    // screen, and the host Start auto-trace gave the monitor was traced every round until SysManager closed. None of
    // these starts the monitor or sends anything: they hand the state a route and read what it shows.

    private static NetworkSharedState NewState() => new(new Services.PingMonitorService(), new Services.TracerouteService(),
        new Services.TracerouteMonitorService(), new Services.SpeedTestService(),
        new Services.NetworkRepairService(new Services.PowerShellRunner()));

    private static List<TracerouteHop> Route(params string[] addresses) =>
        [.. addresses.Select((a, i) => new TracerouteHop { HopNumber = i + 1, Address = a, LatencyMs = 10 + i, Status = "TtlExpired" })];

    private static int LinesFor(NetworkSharedState state, string host) =>
        state.TraceSeries.Count(s => s.Name?.Contains($"({host})") == true);

    [Fact]
    public void ARouteToAHostThatIsNotAPingTarget_IsListedAndDrawn()
    {
        using var state = NewState();
        var lines = state.TraceSeries.Count;
        var route = Route("192.0.2.1", "198.51.100.7", "203.0.113.9");

        state.ApplyRoute("bbc.co.uk", route);

        Assert.Equal(route, state.TracerouteHops);
        Assert.Equal(lines + 1, state.TraceSeries.Count);
        Assert.Equal(1, LinesFor(state, "bbc.co.uk"));
        Assert.Equal([1d, 2d, 3d], state.TraceBuffers["bbc.co.uk"].Select(p => p.X ?? 0));
    }

    [Fact]
    public void AnotherHostTraced_TakesTheLastOnesPlace()
    {
        using var state = NewState();
        var lines = state.TraceSeries.Count;
        state.ApplyRoute("bbc.co.uk", Route("192.0.2.1"));

        var second = Route("198.51.100.7", "203.0.113.9");
        state.ApplyRoute("example.org", second);

        Assert.Equal(second, state.TracerouteHops);
        Assert.Equal(lines + 1, state.TraceSeries.Count);
        Assert.Equal(0, LinesFor(state, "bbc.co.uk"));
        Assert.False(state.TraceBuffers.ContainsKey("bbc.co.uk"));
    }

    [Fact]
    public void APingTargetTypedInAnotherCase_IsThatTarget()
    {
        using var state = NewState();
        Assert.Contains(state.Targets, t => t.Host == "google.com");   // the premise: the Global preset has it
        var lines = state.TraceSeries.Count;
        var route = Route("192.0.2.1", "198.51.100.7");

        state.ApplyRoute("GOOGLE.COM", route);

        Assert.Equal(route, state.TracerouteHops);
        Assert.Equal(lines, state.TraceSeries.Count);
        Assert.Equal(2, state.TraceBuffers["google.com"].Count);
    }

    [Fact]
    public void AddingTheTracedHostAsATarget_LeavesItOneLine()
    {
        using var state = NewState();
        state.ApplyRoute("bbc.co.uk", Route("192.0.2.1"));

        state.AddTarget("BBC", "bbc.co.uk", isCustom: true);

        Assert.Equal(1, LinesFor(state, "bbc.co.uk"));
        Assert.Equal(state.Targets.Count, state.TraceSeries.Count);
    }

    [Fact]
    public void AnAutoTraceHost_ReplacesTheLastOne_AndGoesWhenForgotten()
    {
        using var state = NewState();

        state.TrackAutoTraceHost("a.example");
        state.TrackAutoTraceHost("b.example");

        Assert.False(state.TraceMonitor.Targets.ContainsKey("a.example"));
        Assert.True(state.TraceMonitor.Targets.ContainsKey("b.example"));

        state.ForgetAutoTraceHost();
        Assert.False(state.TraceMonitor.Targets.ContainsKey("b.example"));
    }

    [Fact]
    public void APingTarget_IsNotGivenToTheMonitorAgain_NorTakenOffIt()
    {
        using var state = NewState();
        var watched = state.TraceMonitor.Targets.Count;

        state.TrackAutoTraceHost("GOOGLE.COM");
        Assert.Equal(watched, state.TraceMonitor.Targets.Count);

        state.ForgetAutoTraceHost();
        Assert.True(state.TraceMonitor.Targets.ContainsKey("google.com"));
    }

    [Fact]
    public void AnAutoTraceHostThatBecameAPingTarget_StaysOnTheMonitor()
    {
        using var state = NewState();
        state.TrackAutoTraceHost("a.example");
        state.AddTarget("A", "a.example", isCustom: true);

        state.ForgetAutoTraceHost();

        Assert.True(state.TraceMonitor.Targets.ContainsKey("a.example"));
    }

    [Fact]
    public void PingsStop_TakesTheAutoTraceHostOffTheMonitorToo()
    {
        // Stop on Ping stops the same monitor, and nothing here was started, so nothing is sent.
        using var state = NewState();
        state.TrackAutoTraceHost("a.example");

        state.StopMonitoring();

        Assert.False(state.TraceMonitor.Targets.ContainsKey("a.example"));
    }

    [Fact]
    public void TheChartWindows_ReadInMinutes()
    {
        // The picker listed 60, 300, 600 and 900 with no unit, so 300 could have been seconds, samples or minutes
        // (#2884).
        using var state = NewState();

        Assert.Equal(new[] { 60, 300, 600, 900 }, state.WindowOptions.Select(o => o.Seconds));
        Assert.Equal(new[] { "1 min", "5 min", "10 min", "15 min" }, state.WindowOptions.Select(o => o.Label));
        // What a screen reader says for an entry.
        Assert.Equal("5 min", state.WindowOptions[1].ToString());
        Assert.Contains(state.WindowOptions, o => o.Seconds == state.WindowSeconds);
    }
}
