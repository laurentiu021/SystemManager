// SysManager · QaAuditTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Adversarial tests written from a senior QA perspective — contract
/// violations, race conditions, resource leaks, invalid inputs, and edge
/// cases that a UI user could realistically hit on a 24/7 monitoring host.
/// </summary>
[Collection("Network")]
public class QaAuditTests
{
    private const string Unreachable = "192.0.2.1";

    // ==================================================================
    //  PingMonitorService — invalid input defense
    // ==================================================================

    [Fact]
    public async Task PingMonitor_ZeroInterval_DoesNotBusyLoop()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.Zero,
            TimeoutMs = 300
        };
        svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111"));
        long count = 0;
        svc.SampleReceived += _ => Interlocked.Increment(ref count);

        svc.Start();
        await Task.Delay(500);
        svc.Stop();

        // A zero interval must not translate into a busy loop that issues
        // millions of pings. Cap at a reasonable upper bound (200/500ms).
        var total = Interlocked.Read(ref count);
        Assert.True(total < 200,
            $"Zero interval caused {total} samples in 500ms — possible busy loop");
    }

    [Fact]
    public async Task PingMonitor_NegativeInterval_IsNormalizedOrDoesNotCrash()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(-500),
            TimeoutMs = 300
        };
        svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111"));
        svc.SampleReceived += _ => { };

        var ex = await Record.ExceptionAsync(async () =>
        {
            svc.Start();
            await Task.Delay(300);
            svc.Stop();
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task PingMonitor_ZeroTimeout_DoesNotHang()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 0
        };
        svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111"));
        var tcs = new TaskCompletionSource();
        svc.SampleReceived += _ => tcs.TrySetResult();

        svc.Start();
        // Should produce at least one sample (possibly as a timeout-error) within 3s.
        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        svc.Stop();

        Assert.Same(tcs.Task, done);
    }

    [Fact]
    public async Task PingMonitor_ConcurrentStartStop_IsSafe()
    {
        // Hammer Start/Stop from many threads to check for race on _cts / _loop.
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(50),
            TimeoutMs = 300
        };
        svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111"));

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token;
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { svc.Start(); } catch { }
                try { svc.Stop(); } catch { }
            }
        })).ToArray();

        var ex = await Record.ExceptionAsync(async () => await Task.WhenAll(workers));
        Assert.Null(ex);
        svc.Stop();
    }

    [Fact]
    public async Task PingMonitor_ConcurrentTargetMutation_IsSafe()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 400
        };
        svc.SampleReceived += _ => { };
        svc.Start();

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token;
        var adders = Task.Run(() =>
        {
            var rnd = new Random(1);
            while (!ct.IsCancellationRequested)
                svc.AddOrUpdate(new PingTarget("x", $"192.0.2.{rnd.Next(2, 254)}", "#111"));
        });
        var removers = Task.Run(() =>
        {
            var rnd = new Random(2);
            while (!ct.IsCancellationRequested)
                svc.Remove($"192.0.2.{rnd.Next(2, 254)}");
        });
        var togglers = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var t in svc.Targets.Values)
                    t.IsEnabled = !t.IsEnabled;
            }
        });

        var ex = await Record.ExceptionAsync(async () =>
            await Task.WhenAll(adders, removers, togglers));
        svc.Stop();
        Assert.Null(ex);
    }

    [Fact]
    public async Task PingMonitor_ManyTargets_DoesNotLeakPingObjects()
    {
        // 50 targets at 100ms over 2s = up to 1000 Ping objects. They must all
        // be disposed cleanly (each is in a using). Test for no crash + process
        // still responsive. We measure by running a known-fast ping after.
        using (var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 300
        })
        {
            for (int i = 2; i < 52; i++)
                svc.AddOrUpdate(new PingTarget($"t{i}", $"192.0.2.{i}", "#111"));
            svc.SampleReceived += _ => { };
            svc.Start();
            await Task.Delay(2000);
            svc.Stop();
        }

        // Sanity: a subsequent quick ping still works.
        using var ping = new System.Net.NetworkInformation.Ping();
        var reply = await ping.SendPingAsync("127.0.0.1", 500);
        Assert.Equal(System.Net.NetworkInformation.IPStatus.Success, reply.Status);
    }

    // ==================================================================
    //  PingMonitorService — SampleReceived exception safety
    // ==================================================================

    [Fact]
    public async Task PingMonitor_SubscriberThrows_DoesNotKillPump()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 400
        };
        svc.AddOrUpdate(new PingTarget("x", Unreachable, "#111"));

        long goodCount = 0;
        svc.SampleReceived += _ => throw new InvalidOperationException("handler is nasty");
        svc.SampleReceived += _ => Interlocked.Increment(ref goodCount);

        svc.Start();
        await Task.Delay(700);
        svc.Stop();

        // Pump must keep running despite the throwing subscriber.
        Assert.True(Interlocked.Read(ref goodCount) > 0,
            "Second subscriber should keep getting samples even if first throws");
    }

    // ==================================================================
    //  TracerouteService — defensive inputs
    // ==================================================================

    [Fact]
    public async Task Traceroute_ZeroMaxHops_CompletesQuickly()
    {
        var svc = new TracerouteService { MaxHops = 0, TimeoutMs = 500, ProbesPerHop = 1 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var hops = await svc.RunAsync("192.0.2.1", cts.Token);
        Assert.Empty(hops);
    }

    [Fact]
    public async Task Traceroute_NegativeMaxHops_DoesNotCrash()
    {
        var svc = new TracerouteService { MaxHops = -5, TimeoutMs = 500, ProbesPerHop = 1 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var hops = await svc.RunAsync("192.0.2.1", cts.Token);
        Assert.Empty(hops);
    }

    [Fact]
    public async Task Traceroute_HopSubscriberThrows_DoesNotKillRun()
    {
        var svc = new TracerouteService { MaxHops = 2, TimeoutMs = 400, ProbesPerHop = 1 };
        svc.HopCompleted += _ => throw new InvalidOperationException("bad handler");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Current implementation swallows subscriber exceptions inside RunAsync?
        // If not — this test exposes that. It must complete without bubbling.
        var ex = await Record.ExceptionAsync(async () =>
            await svc.RunAsync("192.0.2.1", cts.Token));
        Assert.Null(ex);
    }

    // ==================================================================
    //  HealthAnalyzer — thresholds boundary conditions
    // ==================================================================

    [Fact]
    public void HealthAnalyzer_LossExactlyAtThreshold_IsBad()
    {
        var m = new HealthAnalyzer.TargetMetric(
            "x", TargetRole.Gateway, 2, 1,
            HealthAnalyzer.LossWarnPercent, 10);
        var diag = HealthAnalyzer.Analyze(new[] { m });
        Assert.Equal(HealthVerdict.LocalNetwork, diag.Verdict);
    }

    [Fact]
    public void HealthAnalyzer_JitterExactlyAtThreshold_IsBad()
    {
        var m = new HealthAnalyzer.TargetMetric(
            "x", TargetRole.Gateway, 2,
            HealthAnalyzer.JitterWarnMs, 0, 10);
        var diag = HealthAnalyzer.Analyze(new[] { m });
        Assert.Equal(HealthVerdict.LocalNetwork, diag.Verdict);
    }

    [Fact]
    public void HealthAnalyzer_JustBelowThreshold_IsGood()
    {
        var m = new HealthAnalyzer.TargetMetric(
            "gw", TargetRole.Gateway, 2,
            HealthAnalyzer.JitterWarnMs - 0.001,
            HealthAnalyzer.LossWarnPercent - 0.001, 10);
        var diag = HealthAnalyzer.Analyze(new[] { m });
        Assert.Equal(HealthVerdict.Good, diag.Verdict);
    }

    [Fact]
    public void HealthAnalyzer_NullAverage_WithHighLoss_StillReportsBad()
    {
        var m = new HealthAnalyzer.TargetMetric(
            "gw", TargetRole.Gateway, null, null, 50, 10);
        var diag = HealthAnalyzer.Analyze(new[] { m });
        Assert.Equal(HealthVerdict.LocalNetwork, diag.Verdict);
    }

    [Fact]
    public void HealthAnalyzer_EmptyMetricsList_DoesNotThrow()
    {
        var diag = HealthAnalyzer.Analyze(Enumerable.Empty<HealthAnalyzer.TargetMetric>());
        Assert.Equal(HealthVerdict.Unknown, diag.Verdict);
    }

    [Fact]
    public void HealthAnalyzer_AllNullAverages_NoCrash()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            new HealthAnalyzer.TargetMetric("a", TargetRole.Gateway, null, null, 0, 10),
            new HealthAnalyzer.TargetMetric("b", TargetRole.PublicDns, null, null, 0, 10),
        });
        Assert.Equal(0, diag.AveragePingMs);
    }

    // ==================================================================
    //  EventLogService — XPath injection / edge cases
    // ==================================================================

    [Fact]
    public void EventLogService_BuildXPath_EscapesProviderNameQuotes()
    {
        var opt = new EventLogQueryOptions
        {
            ProviderName = "Evil'Provider"
        };
        var m = typeof(EventLogService).GetMethod(
            "BuildXPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var xpath = (string)m.Invoke(null, new object[] { opt })!;

        // No unescaped single quotes inside the Provider[@Name='...'] clause.
        Assert.DoesNotContain("'Evil'Provider'", xpath);
    }

    [Fact]
    public void EventLogService_BuildXPath_CombinesAllFilters()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = new() { EventSeverity.Error },
            Since = DateTime.UtcNow.AddHours(-1),
            ProviderName = "X",
            EventId = 42
        };
        var m = typeof(EventLogService).GetMethod(
            "BuildXPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var xpath = (string)m.Invoke(null, new object[] { opt })!;

        Assert.Contains("Level=2", xpath);
        Assert.Contains("TimeCreated", xpath);
        Assert.Contains("Provider[@Name='X']", xpath);
        Assert.Contains("EventID=42", xpath);
    }

    [Fact]
    public void EventLogService_BuildXPath_EmptyFilters_MatchesAll()
    {
        var opt = new EventLogQueryOptions();
        var m = typeof(EventLogService).GetMethod(
            "BuildXPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var xpath = (string)m.Invoke(null, new object[] { opt })!;
        Assert.Equal("*", xpath);
    }

    // ==================================================================
    //  LogsViewModel — double-refresh CTS lifecycle
    // ==================================================================

    [Fact]
    public async Task LogsViewModel_DoubleRefresh_SecondCancelsFirstCleanly()
    {
        var vm = new LogsViewModel();
        vm.SelectedLog = "Bogus-Log-For-Test";
        // Fire two refreshes near-simultaneously; must not throw ObjectDisposedException.
        var t1 = vm.RefreshCommand.ExecuteAsync(null);
        var t2 = vm.RefreshCommand.ExecuteAsync(null);
        await Task.WhenAll(t1, t2);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LogsViewModel_CancelWhileRefreshing_IsSafe()
    {
        var vm = new LogsViewModel();
        vm.SelectedLog = "System";
        vm.SelectedMaxResults = "5";
        var task = vm.RefreshCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        await task; // should not throw
        Assert.False(vm.IsBusy);
    }

    // ==================================================================
    //  NetworkViewModel — FlushPending contract
    // ==================================================================

    [Fact]
    public void NetworkViewModel_FlushDoesNotFlipOrderOfTargets()
    {
        var vm = new NetworkViewModel();
        var originalOrder = vm.Targets.Select(t => t.Host).ToList();

        var m = typeof(NetworkViewModel).GetMethod("OnSample", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var host in originalOrder)
            m.Invoke(vm, new object[] { new PingSample(DateTime.UtcNow, host, 10, "OK") });

        // Target order is stable across flushes.
        Assert.Equal(originalOrder, vm.Targets.Select(t => t.Host).ToList());
    }

    [Fact]
    public void NetworkViewModel_SwitchPreset_WhileSamplesPending_DoesNotLeakStaleData()
    {
        var vm = new NetworkViewModel();
        var m = typeof(NetworkViewModel).GetMethod("OnSample", BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var t in vm.Targets)
            m.Invoke(vm, new object[] { new PingSample(DateTime.UtcNow, t.Host, 99, "OK") });

        vm.SelectedPreset = TargetPresets.Streaming;

        // After preset switch, former preset's hosts must not still live in Targets.
        Assert.All(vm.Targets, t =>
            Assert.True(t.Role == TargetRole.Gateway || t.IsCustom ||
                        TargetPresets.Streaming.Targets.Any(pt => pt.Host == t.Host)));
    }

    [Fact]
    public void NetworkViewModel_PresetSwitchTwice_SameTarget_NotDuplicated()
    {
        var vm = new NetworkViewModel();
        vm.SelectedPreset = TargetPresets.CS2Europe;
        vm.SelectedPreset = TargetPresets.CS2Europe;
        var cs2Hosts = vm.Targets.Where(t => t.Host.StartsWith("146.66.") || t.Host.StartsWith("155.133."))
            .Select(t => t.Host).ToList();
        Assert.Equal(cs2Hosts.Count, cs2Hosts.Distinct().Count());
    }

    [Fact]
    public void NetworkViewModel_IntervalClampedByService_NotNegativeInUi()
    {
        // UI allows entering negative; service clamps to ≥1s. Verify no crash.
        var vm = new NetworkViewModel();
        vm.IntervalSeconds = -999;
        vm.StartCommand.Execute(null);
        vm.StopCommand.Execute(null);
    }

    // ==================================================================
    //  ConsoleViewModel — high throughput
    // ==================================================================

    [Fact]
    public void ConsoleViewModel_10kAppends_StaysCapped()
    {
        var vm = new ConsoleViewModel();
        for (int i = 0; i < 10_000; i++)
            vm.Append(new PowerShellLine(OutputKind.Output, $"{i}", DateTime.Now));
        Assert.True(vm.Lines.Count <= 5000);
    }

    // ==================================================================
    //  AppPackage & models — ToString / ignore case
    // ==================================================================

    [Fact]
    public void AppPackage_DifferentFlagCase_DoesNotCollideInStatus()
    {
        var a = new AppPackage { Status = "PENDING" };
        var b = new AppPackage { Status = "pending" };
        Assert.NotEqual(a.Status, b.Status);
    }

    // ==================================================================
    //  GatewayHelper — return value shape
    // ==================================================================

    [Fact]
    public void GatewayHelper_ReturnValue_IsParseable_WhenNotNull()
    {
        var gw = SysManager.Helpers.GatewayHelper.DetectDefaultGateway();
        if (gw == null) return;
        Assert.True(System.Net.IPAddress.TryParse(gw, out _));
    }

    // ==================================================================
    //  EventLogQueryOptions — negative / extreme MaxResults
    // ==================================================================

    [Fact]
    public async Task EventLogService_MaxResultsZero_YieldsNothing()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions { LogName = "System", MaxResults = 0 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var count = 0;
        await foreach (var _ in svc.ReadAsync(opt, cts.Token)) count++;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task EventLogService_MaxResultsNegative_YieldsNothing()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions { LogName = "System", MaxResults = -10 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var count = 0;
        await foreach (var _ in svc.ReadAsync(opt, cts.Token)) count++;
        Assert.Equal(0, count);
    }

    // ==================================================================
    //  LogsViewModel — select item stability
    // ==================================================================

    [Fact]
    public void LogsViewModel_SetSelectedEntryToNull_IsSafe()
    {
        var vm = new LogsViewModel();
        vm.SelectedEntry = new FriendlyEventEntry { EventId = 1 };
        vm.SelectedEntry = null;
        Assert.Null(vm.SelectedEntry);
    }

    [Fact]
    public void LogsViewModel_CopySelected_NullEntry_StatusIsNotCorrupted()
    {
        var vm = new LogsViewModel();
        vm.StatusMessage = "initial";
        vm.CopySelectedCommand.Execute(null);
        Assert.Equal("initial", vm.StatusMessage);
    }
}
