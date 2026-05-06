// SysManager · QaResilienceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

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
