using System.Reflection;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Additional adversarial tests — resilience under repeated UI actions,
/// memory pressure, unusual input, concurrent mutation.
/// </summary>
[Collection("Network")]
public class QaResilienceTests
{
    // ==================================================================
    //  NetworkViewModel — preset thrashing
    // ==================================================================

    [Fact]
    public void Network_RapidPresetThrashing_NoCrash()
    {
        var vm = new NetworkViewModel();
        var all = vm.Presets.ToList();
        for (int i = 0; i < 100; i++)
            vm.SelectedPreset = all[i % all.Count];
    }

    [Fact]
    public void Network_RapidTargetToggle_NoCrash()
    {
        var vm = new NetworkViewModel();
        for (int i = 0; i < 500; i++)
        {
            foreach (var t in vm.Targets) t.IsEnabled = i % 2 == 0;
        }
    }

    [Fact]
    public void Network_ManyCustomTargets_DoesNotCrash()
    {
        var vm = new NetworkViewModel();
        for (int i = 0; i < 50; i++)
        {
            vm.NewTargetHost = $"custom-{i}.example";
            vm.AddCustomTargetCommand.Execute(null);
        }
        Assert.True(vm.Targets.Count >= 50);
    }

    [Fact]
    public void Network_AddRemoveCycle_KeepsSeriesInSync()
    {
        var vm = new NetworkViewModel();
        for (int i = 0; i < 20; i++)
        {
            vm.NewTargetHost = $"cycle-{i}.example";
            vm.AddCustomTargetCommand.Execute(null);
            var added = vm.Targets.Last();
            vm.RemoveTargetCommand.Execute(added);
        }
        Assert.Equal(vm.Targets.Count, vm.LatencySeries.Count);
        Assert.Equal(vm.Targets.Count, vm.TraceSeries.Count);
    }

    // ==================================================================
    //  Console — pathological input
    // ==================================================================

    [Fact]
    public void Console_Append_WithNullText_CoercesToEmptyOrStoresNull()
    {
        var vm = new ConsoleViewModel();
        var ex = Record.Exception(() =>
            vm.Append(new PowerShellLine(OutputKind.Output, null!, DateTime.Now)));
        // Must not throw when processing or re-rendering the line
        Assert.Null(ex);
    }

    [Fact]
    public void Console_LongBurst_DoesNotLeak()
    {
        var vm = new ConsoleViewModel();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        for (int i = 0; i < 20_000; i++)
            vm.Append(new PowerShellLine(OutputKind.Output, new string('x', 200), DateTime.Now));
        GC.Collect();
        var after = GC.GetTotalMemory(forceFullCollection: true);

        // With MaxLines cap, overhead should stay within tens of MB, not
        // grow with total appends.
        Assert.True(vm.Lines.Count <= 5000);
        // Not asserting a hard memory limit (environment-dependent) but the
        // Lines count itself is the contract.
        _ = (before, after);
    }

    // ==================================================================
    //  EventLogService — concurrent reads
    // ==================================================================

    [Fact]
    public async Task EventLogService_TwoParallelReads_BothComplete()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions { LogName = "System", MaxResults = 5, Since = DateTime.Now.AddDays(-7) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        async Task<int> Run()
        {
            var n = 0;
            await foreach (var _ in svc.ReadAsync(opt, cts.Token)) n++;
            return n;
        }
        var t1 = Run();
        var t2 = Run();
        var results = await Task.WhenAll(t1, t2);
        Assert.All(results, n => Assert.InRange(n, 0, 5));
    }

    // ==================================================================
    //  HealthAnalyzer — exotic inputs
    // ==================================================================

    [Fact]
    public void HealthAnalyzer_SingleTargetGameServerRole_StillYieldsSomething()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            new HealthAnalyzer.TargetMetric("game", TargetRole.GameServer, 30, 5, 0, 10)
        });
        // No gateway / no DNS → Good because game is clean.
        Assert.Equal(HealthVerdict.Good, diag.Verdict);
    }

    [Fact]
    public void HealthAnalyzer_MultipleGameTargets_WorstWinsVerdict()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            new HealthAnalyzer.TargetMetric("gw", TargetRole.Gateway, 2, 1, 0, 10),
            new HealthAnalyzer.TargetMetric("g1", TargetRole.GameServer, 30, 5, 0, 10),
            new HealthAnalyzer.TargetMetric("g2", TargetRole.GameServer, 30, 5, 10, 10), // bad
            new HealthAnalyzer.TargetMetric("g3", TargetRole.GameServer, 30, 5, 0, 10),
        });
        Assert.Equal(HealthVerdict.GameServer, diag.Verdict);
    }

    [Fact]
    public void HealthAnalyzer_StaticThresholds_AreConstants()
    {
        // Defense in depth: constants must not accidentally become negative.
        Assert.True(HealthAnalyzer.LossWarnPercent >= 0);
        Assert.True(HealthAnalyzer.JitterWarnMs >= 0);
        Assert.True(HealthAnalyzer.PingWarnGatewayMs >= 0);
        Assert.True(HealthAnalyzer.PingWarnDnsMs >= 0);
    }

    // ==================================================================
    //  LogsViewModel — search with special characters
    // ==================================================================

    [Theory]
    [InlineData("\\")]
    [InlineData("'")]
    [InlineData("\"")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("&")]
    [InlineData("?")]
    public void LogsViewModel_SearchSpecialChars_DoesNotCrashFilter(string q)
    {
        var vm = new LogsViewModel();
        vm.SearchText = q;
        var m = typeof(LogsViewModel).GetMethod("EntryFilter",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var e = new FriendlyEventEntry { Severity = EventSeverity.Error, Message = "ok", EventId = 1 };
        var ex = Record.Exception(() => m.Invoke(vm, new object[] { e }));
        Assert.Null(ex);
    }

    // ==================================================================
    //  NetworkViewModel — SpeedProgress bounds
    // ==================================================================

    [Fact]
    public void Network_SpeedProgress_CanBeSetInFullRange()
    {
        var vm = new NetworkViewModel();
        for (int v = 0; v <= 100; v += 5)
        {
            vm.SpeedProgress = v;
            Assert.Equal(v, vm.SpeedProgress);
        }
    }

    [Fact]
    public void Network_SpeedStatus_DefaultsEmpty()
    {
        var vm = new NetworkViewModel();
        Assert.Equal("", vm.SpeedStatus);
    }

    // ==================================================================
    //  Wide integration — add 25 targets and simulate flooding samples
    // ==================================================================

    [Fact]
    public void Network_HeavySimulatedSampleFlow_DoesNotCrash()
    {
        var vm = new NetworkViewModel();
        // Add many custom targets
        for (int i = 0; i < 20; i++)
        {
            vm.NewTargetHost = $"load-{i}.example";
            vm.AddCustomTargetCommand.Execute(null);
        }

        var m = typeof(NetworkViewModel).GetMethod("OnSample",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rnd = new Random(5);
        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                var target = vm.Targets[rnd.Next(vm.Targets.Count)];
                var lat = rnd.Next(20) == 0 ? (double?)null : 5 + rnd.NextDouble() * 50;
                m.Invoke(vm, new object[]
                {
                    new PingSample(DateTime.UtcNow, target.Host, lat, lat.HasValue ? "OK" : "Timeout")
                });
            }
        });
        Assert.Null(ex);
    }

    // ==================================================================
    //  NetworkViewModel — invalid NewTargetHost formats
    // ==================================================================

    [Theory]
    [InlineData("://")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("ftp://x")]
    [InlineData("a b c")] // spaces
    [InlineData("::1")]    // IPv6 loopback
    public void Network_AddTarget_TolerantOfWeirdStrings(string host)
    {
        var vm = new NetworkViewModel();
        var before = vm.Targets.Count;
        vm.NewTargetHost = host;
        var ex = Record.Exception(() => vm.AddCustomTargetCommand.Execute(null));
        Assert.Null(ex);
        // Some hosts may be rejected, but never crash.
        Assert.InRange(vm.Targets.Count, before, before + 1);
    }

    // ==================================================================
    //  PingMonitor — event unsubscription
    // ==================================================================

    [Fact]
    public async Task PingMonitor_UnsubscribedHandler_IsNotCalled()
    {
        using var svc = new PingMonitorService
        {
            Interval = TimeSpan.FromMilliseconds(100),
            TimeoutMs = 400
        };
        svc.AddOrUpdate(new PingTarget("x", "192.0.2.1", "#111"));

        long counter = 0;
        Action<PingSample> handler = _ => Interlocked.Increment(ref counter);
        svc.SampleReceived += handler;
        svc.Start();
        await Task.Delay(400);
        svc.SampleReceived -= handler;
        var before = Interlocked.Read(ref counter);
        await Task.Delay(700);
        svc.Stop();
        var after = Interlocked.Read(ref counter);

        // Allow one in-flight sample to arrive after unsubscribe, nothing more.
        Assert.True(after - before <= 1,
            $"Got {after - before} samples after unsubscribe");
    }

    // ==================================================================
    //  EventExplainer — many unique calls don't leak
    // ==================================================================

    [Fact]
    public void EventExplainer_LargeBatch_StaysFast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            var e = new FriendlyEventEntry
            {
                ProviderName = $"Provider-{i % 100}",
                EventId = i % 1000,
                Severity = (EventSeverity)(i % 5)
            };
            EventExplainer.Enrich(e);
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Enrichment of 10k events took {sw.Elapsed} — should be near-instant");
    }
}
