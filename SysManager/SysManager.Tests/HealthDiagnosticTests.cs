// SysManager · HealthDiagnosticTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="HealthDiagnostic"/> and <see cref="HealthVerdict"/> enum.
/// </summary>
public class HealthDiagnosticTests
{
    [Fact]
    public void Defaults_AreUnknown()
    {
        var h = new HealthDiagnostic();
        Assert.Equal(HealthVerdict.Unknown, h.Verdict);
        Assert.Equal("Waiting for data…", h.Headline);
        Assert.Equal("", h.Detail);
        Assert.Equal("#9AA0A6", h.ColorHex);
        Assert.Equal(0, h.WorstLossPercent);
        Assert.Equal(0, h.WorstJitterMs);
        Assert.Equal(0, h.AveragePingMs);
    }

    [Fact]
    public void PropertyChanged_FiresOnVerdictChange()
    {
        var h = new HealthDiagnostic();
        var changed = new List<string>();
        h.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        h.Verdict = HealthVerdict.Good;
        Assert.Contains("Verdict", changed);
    }

    [Fact]
    public void PropertyChanged_FiresOnHeadlineChange()
    {
        var h = new HealthDiagnostic();
        var changed = new List<string>();
        h.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        h.Headline = "All good";
        Assert.Contains("Headline", changed);
    }

    [Fact]
    public void AllProperties_Settable()
    {
        var h = new HealthDiagnostic
        {
            Verdict = HealthVerdict.LocalNetwork,
            Headline = "Local network issue",
            Detail = "Gateway has high loss",
            ColorHex = "#F59E0B",
            WorstLossPercent = 15.5,
            WorstJitterMs = 8.2,
            AveragePingMs = 45.0
        };
        Assert.Equal(HealthVerdict.LocalNetwork, h.Verdict);
        Assert.Equal(15.5, h.WorstLossPercent);
        Assert.Equal(8.2, h.WorstJitterMs);
        Assert.Equal(45.0, h.AveragePingMs);
    }

    [Theory]
    [InlineData(HealthVerdict.Good)]
    [InlineData(HealthVerdict.LocalNetwork)]
    [InlineData(HealthVerdict.IspOrUpstream)]
    [InlineData(HealthVerdict.GameServer)]
    [InlineData(HealthVerdict.StreamingService)]
    [InlineData(HealthVerdict.Mixed)]
    [InlineData(HealthVerdict.Unknown)]
    public void HealthVerdict_AllValues_Exist(HealthVerdict v)
    {
        var h = new HealthDiagnostic { Verdict = v };
        Assert.Equal(v, h.Verdict);
    }
}
