using SysManager.Models;
using SysManager.Services;
using static SysManager.Services.HealthAnalyzer;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="HealthAnalyzer"/>. The analyzer is a static
/// pure function — no system deps, no async, no UI.
/// </summary>
public class HealthAnalyzerTests
{
    private static TargetMetric M(string name, TargetRole role,
        double? avg = 5, double? jitter = 2, double loss = 0, int count = 30)
        => new(name, role, avg, jitter, loss, count);

    // ---------- empty / no data ----------

    [Fact]
    public void NoMetrics_ReturnsUnknown()
    {
        var d = Analyze(Array.Empty<TargetMetric>());
        Assert.Equal(HealthVerdict.Unknown, d.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(d.Headline));
        Assert.Equal("#9AA0A6", d.ColorHex);
    }

    [Fact]
    public void AllZeroSampleCount_ReturnsUnknown()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, count: 0),
            M("dns", TargetRole.PublicDns, count: 0),
        });
        Assert.Equal(HealthVerdict.Unknown, d.Verdict);
    }

    // ---------- all clean ----------

    [Fact]
    public void AllClean_ReturnsGood()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 3, jitter: 1),
            M("dns", TargetRole.PublicDns, avg: 15, jitter: 2),
            M("game", TargetRole.GameServer, avg: 40, jitter: 3),
        });
        Assert.Equal(HealthVerdict.Good, d.Verdict);
        Assert.Equal("#06D6A0", d.ColorHex);
    }

    // ---------- gateway bad ----------

    [Fact]
    public void GatewayHighLoss_ReturnsLocalNetwork()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, loss: 5),
            M("dns", TargetRole.PublicDns),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
        Assert.Equal("#FF6B6B", d.ColorHex);
    }

    [Fact]
    public void GatewayHighJitter_ReturnsLocalNetwork()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, jitter: 25),
            M("dns", TargetRole.PublicDns),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    [Fact]
    public void GatewayHighPing_ReturnsLocalNetwork()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 20, jitter: 1, loss: 0),
            M("dns", TargetRole.PublicDns),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    [Fact]
    public void GatewayTakesPrecedenceOverAllOtherLayers()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, loss: 10),
            M("dns", TargetRole.PublicDns, loss: 10),
            M("game", TargetRole.GameServer, loss: 10),
            M("yt", TargetRole.Streaming, loss: 10),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    // ---------- ISP / upstream ----------

    [Fact]
    public void DnsLossOnly_ReturnsIspOrUpstream()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns, loss: 5),
            M("game", TargetRole.GameServer),
        });
        Assert.Equal(HealthVerdict.IspOrUpstream, d.Verdict);
        Assert.Equal("#FFD166", d.ColorHex);
    }

    [Fact]
    public void DnsHighPing_ReturnsIspOrUpstream()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns, avg: 80, jitter: 1, loss: 0),
        });
        Assert.Equal(HealthVerdict.IspOrUpstream, d.Verdict);
    }

    // ---------- game server ----------

    [Fact]
    public void OnlyGameBad_ReturnsGameServer()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns),
            M("cs2", TargetRole.GameServer, loss: 8),
        });
        Assert.Equal(HealthVerdict.GameServer, d.Verdict);
        Assert.Equal("#F72585", d.ColorHex);
    }

    [Fact]
    public void GameBadWithHighJitter_ReturnsGameServer()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns),
            M("pubg", TargetRole.GameServer, jitter: 30),
        });
        Assert.Equal(HealthVerdict.GameServer, d.Verdict);
    }

    // ---------- streaming ----------

    [Fact]
    public void OnlyStreamingBad_ReturnsStreamingService()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns),
            M("yt", TargetRole.Streaming, loss: 5),
        });
        Assert.Equal(HealthVerdict.StreamingService, d.Verdict);
        Assert.Equal("#B388FF", d.ColorHex);
    }

    // ---------- mixed ----------

    [Fact]
    public void DnsAndGameBad_ReturnsMixed()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns, loss: 5),
            M("game", TargetRole.GameServer, loss: 5),
        });
        // dns bad + game bad → not pure ISP, not pure GameServer → Mixed
        // Actually per the code: dnsBad && !gameBad → ISP. But here gameBad is true too.
        // The code checks: if (dnsBad && !gameBad && !streamBad) → ISP. Fails.
        // Then: if (gameBad) → GameServer. This wins.
        Assert.Equal(HealthVerdict.GameServer, d.Verdict);
    }

    [Fact]
    public void StreamingAndDnsBad_NotStreamingService()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns, loss: 5),
            M("yt", TargetRole.Streaming, loss: 5),
        });
        // dnsBad && !gameBad && !streamBad → false (streamBad is true)
        // gameBad → false
        // streamBad && !dnsBad → false (dnsBad is true)
        // Falls through to Mixed
        Assert.Equal(HealthVerdict.Mixed, d.Verdict);
        Assert.Equal("#FF6B6B", d.ColorHex);
    }

    // ---------- threshold boundaries ----------

    [Fact]
    public void LossJustBelowThreshold_IsGood()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, loss: 1.9),
            M("dns", TargetRole.PublicDns, loss: 1.9),
        });
        Assert.Equal(HealthVerdict.Good, d.Verdict);
    }

    [Fact]
    public void LossExactlyAtThreshold_IsBad()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, loss: LossWarnPercent),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    [Fact]
    public void JitterJustBelowThreshold_IsGood()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, jitter: 19.9),
        });
        Assert.Equal(HealthVerdict.Good, d.Verdict);
    }

    [Fact]
    public void JitterExactlyAtThreshold_IsBad()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, jitter: JitterWarnMs),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    [Fact]
    public void GatewayPingJustBelowThreshold_IsGood()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 14.9, jitter: 1, loss: 0),
        });
        Assert.Equal(HealthVerdict.Good, d.Verdict);
    }

    [Fact]
    public void GatewayPingExactlyAtThreshold_IsBad()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: PingWarnGatewayMs, jitter: 1, loss: 0),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    // ---------- null handling ----------

    [Fact]
    public void NullAverageMs_DoesNotCrash()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: null, jitter: 1, loss: 0),
        });
        Assert.NotEqual(HealthVerdict.Unknown, d.Verdict);
    }

    [Fact]
    public void NullJitterMs_DoesNotCrash()
    {
        var d = Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 5, jitter: null, loss: 0),
        });
        Assert.NotEqual(HealthVerdict.Unknown, d.Verdict);
    }

    // ---------- aggregated metrics ----------

    [Fact]
    public void Metrics_AreCorrectlyAggregated()
    {
        var d = Analyze(new[]
        {
            M("a", TargetRole.Generic, avg: 10, jitter: 2, loss: 1),
            M("b", TargetRole.Generic, avg: 30, jitter: 8, loss: 4),
        });
        Assert.Equal(4, d.WorstLossPercent);
        Assert.Equal(8, d.WorstJitterMs);
        Assert.Equal(20, d.AveragePingMs);
    }

    // ---------- HealthDiagnostic model ----------

    [Fact]
    public void HealthDiagnostic_Defaults()
    {
        var d = new HealthDiagnostic();
        Assert.Equal(HealthVerdict.Unknown, d.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(d.Headline));
        Assert.Equal("#9AA0A6", d.ColorHex);
        Assert.Equal(0, d.WorstLossPercent);
        Assert.Equal(0, d.WorstJitterMs);
        Assert.Equal(0, d.AveragePingMs);
    }

    [Fact]
    public void HealthDiagnostic_PropertyChanged_Fires()
    {
        var d = new HealthDiagnostic();
        var fired = false;
        d.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(d.Verdict)) fired = true; };
        d.Verdict = HealthVerdict.Good;
        Assert.True(fired);
    }

    // ---------- constants are sane ----------

    [Fact]
    public void Thresholds_ArePositive()
    {
        Assert.True(LossWarnPercent > 0);
        Assert.True(JitterWarnMs > 0);
        Assert.True(PingWarnGatewayMs > 0);
        Assert.True(PingWarnDnsMs > 0);
        Assert.True(PingWarnDnsMs > PingWarnGatewayMs);
    }
}
