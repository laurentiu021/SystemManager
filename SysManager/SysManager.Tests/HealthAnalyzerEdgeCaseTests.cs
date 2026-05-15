// SysManager · HealthAnalyzer edge-case and boundary tests
using SysManager.Models;
using SysManager.Services;
using static SysManager.Services.HealthAnalyzer;

namespace SysManager.Tests;

public class HealthAnalyzerEdgeCaseTests
{
    [Fact]
    public void Analyze_EmptyMetrics_ReturnsUnknown()
    {
        var result = HealthAnalyzer.Analyze([]);
        Assert.Equal(HealthVerdict.Unknown, result.Verdict);
        Assert.Equal("#9AA0A6", result.ColorHex);
    }

    [Fact]
    public void Analyze_AllZeroSamples_ReturnsUnknown()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 5, 1, 0, 0),
            new TargetMetric("dns", TargetRole.PublicDns, 10, 2, 0, 0),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.Unknown, result.Verdict);
    }

    [Fact]
    public void Analyze_AllGood_ReturnsGood()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 1.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 3.0, 0, 10),
            new TargetMetric("game", TargetRole.GameServer, 30.0, 5.0, 0, 10),
            new TargetMetric("stream", TargetRole.Streaming, 20.0, 4.0, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.Good, result.Verdict);
        Assert.Equal("#06D6A0", result.ColorHex);
    }

    [Fact]
    public void Analyze_GatewayBad_ReturnsLocalNetwork()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 20.0, 5.0, 3.0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 3.0, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.LocalNetwork, result.Verdict);
        Assert.Equal("#FF6B6B", result.ColorHex);
    }

    [Fact]
    public void Analyze_GatewayHighPing_ReturnsLocalNetwork()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, PingWarnGatewayMs, 1.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 3.0, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.LocalNetwork, result.Verdict);
    }

    [Fact]
    public void Analyze_GatewayJustBelowThreshold_Good()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, PingWarnGatewayMs - 0.1, JitterWarnMs - 0.1, LossWarnPercent - 0.1, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 3.0, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.Good, result.Verdict);
    }

    [Fact]
    public void Analyze_DnsBadOnly_ReturnsIspOrUpstream()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 1.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, PingWarnDnsMs, 5.0, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.IspOrUpstream, result.Verdict);
        Assert.Equal("#FFD166", result.ColorHex);
    }

    [Fact]
    public void Analyze_DnsHighLoss_ReturnsIspOrUpstream()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 1.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 20.0, 5.0, LossWarnPercent, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.IspOrUpstream, result.Verdict);
    }

    [Fact]
    public void Analyze_GameBadOnly_ReturnsGameServer()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 1.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 3.0, 0, 10),
            new TargetMetric("game", TargetRole.GameServer, 80.0, JitterWarnMs, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.GameServer, result.Verdict);
        Assert.Equal("#F72585", result.ColorHex);
    }

    [Fact]
    public void Analyze_StreamBadOnly_ReturnsStreamingService()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 1.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 3.0, 0, 10),
            new TargetMetric("stream", TargetRole.Streaming, 50.0, JitterWarnMs, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.StreamingService, result.Verdict);
        Assert.Equal("#B388FF", result.ColorHex);
    }

    [Fact]
    public void Analyze_DnsAndGameBad_ReturnsMixed()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 1.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, PingWarnDnsMs, 5.0, 0, 10),
            new TargetMetric("game", TargetRole.GameServer, 80.0, JitterWarnMs + 5, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        // FUNC-M2: When dns AND game are bad, return Mixed — not GameServer,
        // because saying "DNS is clean" would be incorrect.
        Assert.Equal(HealthVerdict.Mixed, result.Verdict);
    }

    [Fact]
    public void Analyze_DnsAndStreamBad_ReturnsMixed()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 1.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, PingWarnDnsMs, 5.0, 0, 10),
            new TargetMetric("stream", TargetRole.Streaming, 50.0, JitterWarnMs, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.Mixed, result.Verdict);
        Assert.Equal("#FF6B6B", result.ColorHex);
    }

    [Fact]
    public void Analyze_NullLatency_StillProcesses()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, null, null, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, null, null, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.Good, result.Verdict);
        Assert.Equal(0, result.AveragePingMs);
    }

    [Fact]
    public void Analyze_HighJitter_TriggersWarning()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 5.0, JitterWarnMs, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 3.0, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(HealthVerdict.LocalNetwork, result.Verdict);
    }

    [Fact]
    public void Analyze_WorstLoss_ComputedCorrectly()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 1.0, 0.5, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 3.0, 1.5, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(1.5, result.WorstLossPercent);
    }

    [Fact]
    public void Analyze_WorstJitter_ComputedCorrectly()
    {
        var metrics = new[]
        {
            new TargetMetric("gw", TargetRole.Gateway, 2.0, 5.0, 0, 10),
            new TargetMetric("dns", TargetRole.PublicDns, 15.0, 12.0, 0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(12.0, result.WorstJitterMs);
    }

    [Fact]
    public void Analyze_OnlyGenericRole_AllGood()
    {
        var metrics = new[]
        {
            new TargetMetric("custom", TargetRole.Generic, 100.0, 50.0, 10.0, 10),
        };
        var result = HealthAnalyzer.Analyze(metrics);
        // Generic role is not checked by any of the specific conditions
        Assert.Equal(HealthVerdict.Good, result.Verdict);
    }
}
