// SysManager · EnumCoverageTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class EnumCoverageTests
{
    // ---------- TargetRole ----------

    [Fact]
    public void TargetRole_HasAllKnownValues()
    {
        var values = Enum.GetValues(typeof(TargetRole)).Cast<TargetRole>().ToList();
        Assert.Contains(TargetRole.Generic, values);
        Assert.Contains(TargetRole.Gateway, values);
        Assert.Contains(TargetRole.PublicDns, values);
        Assert.Contains(TargetRole.GameServer, values);
        Assert.Contains(TargetRole.Streaming, values);
    }

    [Theory]
    [InlineData(TargetRole.Generic)]
    [InlineData(TargetRole.Gateway)]
    [InlineData(TargetRole.PublicDns)]
    [InlineData(TargetRole.GameServer)]
    [InlineData(TargetRole.Streaming)]
    public void TargetRole_Roundtrip(TargetRole role)
    {
        var s = role.ToString();
        Assert.True(Enum.TryParse<TargetRole>(s, out var parsed));
        Assert.Equal(role, parsed);
    }

    // ---------- EventSeverity ----------

    [Fact]
    public void EventSeverity_Ordering_GoesHigherForWorse()
    {
        // Verbose < Info < Warning < Error < Critical
        Assert.True((int)EventSeverity.Verbose < (int)EventSeverity.Info);
        Assert.True((int)EventSeverity.Info < (int)EventSeverity.Warning);
        Assert.True((int)EventSeverity.Warning < (int)EventSeverity.Error);
        Assert.True((int)EventSeverity.Error < (int)EventSeverity.Critical);
    }

    [Theory]
    [InlineData(EventSeverity.Verbose)]
    [InlineData(EventSeverity.Info)]
    [InlineData(EventSeverity.Warning)]
    [InlineData(EventSeverity.Error)]
    [InlineData(EventSeverity.Critical)]
    public void EventSeverity_Roundtrip(EventSeverity sev)
    {
        var s = sev.ToString();
        Assert.True(Enum.TryParse<EventSeverity>(s, out var parsed));
        Assert.Equal(sev, parsed);
    }

    // ---------- HealthVerdict ----------

    [Fact]
    public void HealthVerdict_HasAllKnownValues()
    {
        var values = Enum.GetValues(typeof(HealthVerdict)).Cast<HealthVerdict>().ToList();
        Assert.Contains(HealthVerdict.Good, values);
        Assert.Contains(HealthVerdict.LocalNetwork, values);
        Assert.Contains(HealthVerdict.IspOrUpstream, values);
        Assert.Contains(HealthVerdict.GameServer, values);
        Assert.Contains(HealthVerdict.StreamingService, values);
        Assert.Contains(HealthVerdict.Mixed, values);
        Assert.Contains(HealthVerdict.Unknown, values);
    }

    // ---------- OutputKind ----------

    [Fact]
    public void OutputKind_HasAllKnownValues()
    {
        var values = Enum.GetValues(typeof(OutputKind)).Cast<OutputKind>().ToList();
        Assert.Contains(OutputKind.Info, values);
        Assert.Contains(OutputKind.Output, values);
        Assert.Contains(OutputKind.Warning, values);
        Assert.Contains(OutputKind.Error, values);
        Assert.Contains(OutputKind.Verbose, values);
        Assert.Contains(OutputKind.Debug, values);
        Assert.Contains(OutputKind.Progress, values);
    }
}
