using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

public class HealthAnalyzerTests
{
    private static HealthAnalyzer.TargetMetric M(string name, TargetRole role,
        double avg = 5, double jitter = 2, double loss = 0, int count = 30)
        => new(name, role, avg, jitter, loss, count);

    [Fact]
    public void NoData_YieldsUnknown()
    {
        var d = HealthAnalyzer.Analyze(Array.Empty<HealthAnalyzer.TargetMetric>());
        Assert.Equal(HealthVerdict.Unknown, d.Verdict);
    }

    [Fact]
    public void AllClean_YieldsGood()
    {
        var d = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 3, jitter: 1),
            M("dns", TargetRole.PublicDns, avg: 15, jitter: 2),
            M("game", TargetRole.GameServer, avg: 40, jitter: 3),
        });
        Assert.Equal(HealthVerdict.Good, d.Verdict);
        Assert.Contains("healthy", d.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GatewayLoss_YieldsLocalNetwork()
    {
        var d = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, loss: 10),
            M("dns", TargetRole.PublicDns),
            M("game", TargetRole.GameServer),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    [Fact]
    public void DnsLossButGatewayOk_YieldsIsp()
    {
        var d = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns, loss: 8),
            M("game", TargetRole.GameServer),
        });
        Assert.Equal(HealthVerdict.IspOrUpstream, d.Verdict);
    }

    [Fact]
    public void OnlyGameBad_YieldsGameServer()
    {
        var d = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns),
            M("game1", TargetRole.GameServer, loss: 15),
            M("game2", TargetRole.GameServer, jitter: 50),
        });
        Assert.Equal(HealthVerdict.GameServer, d.Verdict);
        Assert.Contains("game server", d.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyStreamingBad_YieldsStreamingService()
    {
        var d = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns),
            M("yt", TargetRole.Streaming, loss: 6),
        });
        Assert.Equal(HealthVerdict.StreamingService, d.Verdict);
    }

    [Fact]
    public void GatewayTakesPrecedenceOverEverythingElse()
    {
        var d = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, loss: 20),
            M("dns", TargetRole.PublicDns, loss: 20),
            M("game", TargetRole.GameServer, loss: 20),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    [Fact]
    public void HighJitter_CountsAsBad()
    {
        var d = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, jitter: 40),
            M("dns", TargetRole.PublicDns),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, d.Verdict);
    }

    [Fact]
    public void Metrics_AreAggregated()
    {
        var d = HealthAnalyzer.Analyze(new[]
        {
            M("a", TargetRole.Generic, avg: 10, jitter: 2, loss: 1),
            M("b", TargetRole.Generic, avg: 30, jitter: 8, loss: 4),
        });
        Assert.Equal(4, d.WorstLossPercent);
        Assert.Equal(8, d.WorstJitterMs);
        Assert.Equal(20, d.AveragePingMs);
    }
}
