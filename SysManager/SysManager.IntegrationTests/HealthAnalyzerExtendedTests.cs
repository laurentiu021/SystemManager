// SysManager · HealthAnalyzerExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

public class HealthAnalyzerExtendedTests
{
    private static HealthAnalyzer.TargetMetric M(string name, TargetRole role,
        double avg = 5, double jitter = 2, double loss = 0, int count = 30)
        => new(name, role, avg, jitter, loss, count);

    // ---------- Threshold constants ----------

    [Fact]
    public void Thresholds_AreReasonable()
    {
        Assert.True(HealthAnalyzer.LossWarnPercent > 0);
        Assert.True(HealthAnalyzer.LossWarnPercent < 20, "Too lenient");
        Assert.True(HealthAnalyzer.JitterWarnMs > 0);
        Assert.True(HealthAnalyzer.PingWarnGatewayMs < HealthAnalyzer.PingWarnDnsMs,
            "Gateway must be stricter than DNS");
    }

    // ---------- Empty / sampleless ----------

    [Fact]
    public void MetricsWithZeroSamples_AreIgnored()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("dead", TargetRole.Gateway, avg: 9999, loss: 99, count: 0)
        });
        Assert.Equal(HealthVerdict.Unknown, diag.Verdict);
    }

    [Fact]
    public void MixedZeroAndPopulated_KeepsOnlyPopulated()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("zero", TargetRole.Gateway, avg: 9999, loss: 99, count: 0),
            M("gw", TargetRole.Gateway, avg: 3, jitter: 1, loss: 0, count: 20),
        });
        Assert.Equal(HealthVerdict.Good, diag.Verdict);
    }

    // ---------- Good verdict variations ----------

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.5, 18.0)] // both just below thresholds
    public void Good_WhenAllBelowThresholds(double loss, double jitter)
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 2, jitter: jitter / 2, loss: loss),
            M("dns", TargetRole.PublicDns, avg: 20, jitter: jitter, loss: loss),
        });
        Assert.Equal(HealthVerdict.Good, diag.Verdict);
    }

    [Fact]
    public void Good_HeadlineMentionsHealthy()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 2, jitter: 1),
            M("dns", TargetRole.PublicDns, avg: 15, jitter: 2),
        });
        Assert.Contains("healthy", diag.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#06D6A0", diag.ColorHex);
    }

    // ---------- Local network ----------

    [Theory]
    [InlineData(10.0, 5.0)]     // high loss on gateway
    [InlineData(0.0, 30.0)]     // high jitter on gateway
    [InlineData(0.0, 0.0)]      // high average even with no loss/jitter
    public void LocalNetwork_WhenGatewayIsBad(double loss, double jitter)
    {
        var avg = (loss == 0 && jitter == 0) ? 50.0 : 5.0;
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: avg, jitter: jitter, loss: loss),
            M("dns", TargetRole.PublicDns, avg: 15, jitter: 2, loss: 0),
        });
        Assert.Equal(HealthVerdict.LocalNetwork, diag.Verdict);
    }

    [Fact]
    public void LocalNetwork_HeadlineMentionsLocal()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, loss: 15),
            M("dns", TargetRole.PublicDns),
        });
        Assert.Contains("local", diag.Headline, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- ISP ----------

    [Fact]
    public void IspOrUpstream_WhenOnlyDnsIsBad()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 3, jitter: 1, loss: 0),
            M("dns", TargetRole.PublicDns, avg: 20, jitter: 2, loss: 10),
        });
        Assert.Equal(HealthVerdict.IspOrUpstream, diag.Verdict);
    }

    [Fact]
    public void IspOrUpstream_TriggersOnHighDnsPingToo()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway, avg: 2, jitter: 1),
            M("dns", TargetRole.PublicDns, avg: 150, jitter: 5, loss: 0),
        });
        Assert.Equal(HealthVerdict.IspOrUpstream, diag.Verdict);
    }

    // ---------- Game server ----------

    [Fact]
    public void GameServer_WhenGameLossButDnsClean()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns),
            M("game", TargetRole.GameServer, loss: 20),
        });
        Assert.Equal(HealthVerdict.GameServer, diag.Verdict);
    }

    [Fact]
    public void GameServer_DetectsJitterSpike()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns),
            M("game", TargetRole.GameServer, jitter: 40),
        });
        Assert.Equal(HealthVerdict.GameServer, diag.Verdict);
    }

    // ---------- Streaming ----------

    [Fact]
    public void StreamingService_WhenOnlyStreamBad()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns),
            M("yt", TargetRole.Streaming, loss: 8),
        });
        Assert.Equal(HealthVerdict.StreamingService, diag.Verdict);
    }

    // ---------- Mixed ----------

    [Fact]
    public void Mixed_WhenDnsAndGameBad()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns, loss: 5),
            M("game", TargetRole.GameServer, loss: 10),
        });
        Assert.Equal(HealthVerdict.Mixed, diag.Verdict);
    }

    [Fact]
    public void Mixed_WhenDnsAndStreamingBad()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("gw", TargetRole.Gateway),
            M("dns", TargetRole.PublicDns, loss: 5),
            M("yt", TargetRole.Streaming, loss: 5),
        });
        Assert.Equal(HealthVerdict.Mixed, diag.Verdict);
    }

    // ---------- Aggregation ----------

    [Fact]
    public void AveragePing_IsMeanOfAllAvailable()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("a", TargetRole.Generic, avg: 10),
            M("b", TargetRole.Generic, avg: 30),
            M("c", TargetRole.Generic, avg: 20),
        });
        Assert.Equal(20, diag.AveragePingMs);
    }

    [Fact]
    public void WorstLoss_IsMaximum()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("a", TargetRole.Generic, loss: 1),
            M("b", TargetRole.Generic, loss: 5),
            M("c", TargetRole.Generic, loss: 3),
        });
        Assert.Equal(5, diag.WorstLossPercent);
    }

    [Fact]
    public void WorstJitter_IsMaximum()
    {
        var diag = HealthAnalyzer.Analyze(new[]
        {
            M("a", TargetRole.Generic, jitter: 1),
            M("b", TargetRole.Generic, jitter: 15),
            M("c", TargetRole.Generic, jitter: 8),
        });
        Assert.Equal(15, diag.WorstJitterMs);
    }

    // ---------- Verdict colors ----------

    [Theory]
    [InlineData(HealthVerdict.Good, "#06D6A0")]
    [InlineData(HealthVerdict.LocalNetwork, "#FF6B6B")]
    [InlineData(HealthVerdict.IspOrUpstream, "#FFD166")]
    [InlineData(HealthVerdict.GameServer, "#F72585")]
    [InlineData(HealthVerdict.StreamingService, "#B388FF")]
    [InlineData(HealthVerdict.Mixed, "#FF6B6B")]
    [InlineData(HealthVerdict.Unknown, "#9AA0A6")]
    public void AllVerdicts_ProduceValidColor(HealthVerdict expectedVerdict, string expectedColor)
    {
        // Build the conditions to trigger each verdict
        HealthAnalyzer.TargetMetric[] metrics = expectedVerdict switch
        {
            HealthVerdict.Unknown => Array.Empty<HealthAnalyzer.TargetMetric>(),
            HealthVerdict.Good => new[] { M("gw", TargetRole.Gateway, avg: 2, jitter: 1) },
            HealthVerdict.LocalNetwork => new[] { M("gw", TargetRole.Gateway, loss: 15) },
            HealthVerdict.IspOrUpstream => new[]
            {
                M("gw", TargetRole.Gateway, avg: 2, jitter: 1),
                M("dns", TargetRole.PublicDns, loss: 10)
            },
            HealthVerdict.GameServer => new[]
            {
                M("gw", TargetRole.Gateway, avg: 2, jitter: 1),
                M("dns", TargetRole.PublicDns, avg: 15, jitter: 2),
                M("game", TargetRole.GameServer, loss: 10)
            },
            HealthVerdict.StreamingService => new[]
            {
                M("gw", TargetRole.Gateway, avg: 2, jitter: 1),
                M("dns", TargetRole.PublicDns, avg: 15, jitter: 2),
                M("yt", TargetRole.Streaming, loss: 10)
            },
            HealthVerdict.Mixed => new[]
            {
                M("gw", TargetRole.Gateway, avg: 2, jitter: 1),
                M("dns", TargetRole.PublicDns, loss: 10),
                M("game", TargetRole.GameServer, loss: 10)
            },
            _ => throw new InvalidOperationException()
        };
        var diag = HealthAnalyzer.Analyze(metrics);
        Assert.Equal(expectedVerdict, diag.Verdict);
        Assert.Equal(expectedColor, diag.ColorHex);
    }
}
