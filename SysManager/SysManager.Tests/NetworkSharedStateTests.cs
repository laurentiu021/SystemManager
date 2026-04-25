// SysManager · NetworkSharedStateTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class NetworkSharedStateTests
{
    [Fact]
    public void Constructor_SeedsGatewayAndPreset()
    {
        var state = new NetworkSharedState();
        Assert.NotEmpty(state.Targets);
    }

    [Fact]
    public void AddTarget_IgnoresDuplicate()
    {
        var state = new NetworkSharedState();
        var before = state.Targets.Count;
        state.AddTarget("Dup", state.Targets[0].Host);
        Assert.Equal(before, state.Targets.Count);
    }

    [Fact]
    public void AddCustomTarget_EmptyHost_DoesNothing()
    {
        var state = new NetworkSharedState();
        state.NewTargetHost = "";
        var before = state.Targets.Count;
        state.AddCustomTarget();
        Assert.Equal(before, state.Targets.Count);
    }

    [Fact]
    public void AddCustomTarget_ValidHost_AddsTarget()
    {
        var state = new NetworkSharedState();
        state.NewTargetHost = "10.99.99.99";
        var before = state.Targets.Count;
        state.AddCustomTarget();
        Assert.Equal(before + 1, state.Targets.Count);
        Assert.Equal("", state.NewTargetHost);
    }

    [Fact]
    public void RemoveTarget_NonCustom_DoesNothing()
    {
        var state = new NetworkSharedState();
        var first = state.Targets.FirstOrDefault(t => !t.IsCustom);
        if (first == null) return;
        var before = state.Targets.Count;
        state.RemoveTarget(first);
        Assert.Equal(before, state.Targets.Count);
    }

    [Fact]
    public void RemoveTarget_Null_DoesNothing()
    {
        var state = new NetworkSharedState();
        var before = state.Targets.Count;
        state.RemoveTarget(null);
        Assert.Equal(before, state.Targets.Count);
    }

    [Fact]
    public void ClearHistory_ResetsAllTargetStats()
    {
        var state = new NetworkSharedState();
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
        var state = new NetworkSharedState();
        state.ApplyPreset(TargetPresets.All[0]);
        Assert.True(state.Targets.Count > 0);
    }

    [Fact]
    public void Health_IsNotNull()
    {
        var state = new NetworkSharedState();
        Assert.NotNull(state.Health);
    }

    [Fact]
    public void LatencySeries_MatchesTargetCount()
    {
        var state = new NetworkSharedState();
        Assert.Equal(state.Targets.Count, state.LatencySeries.Count);
    }

    [Fact]
    public void TraceSeries_MatchesTargetCount()
    {
        var state = new NetworkSharedState();
        Assert.Equal(state.Targets.Count, state.TraceSeries.Count);
    }

    [Fact]
    public void Presets_NotEmpty()
    {
        var state = new NetworkSharedState();
        Assert.NotEmpty(state.Presets);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var state = new NetworkSharedState();
        Assert.Equal(1, state.IntervalSeconds);
        Assert.Equal(60, state.WindowSeconds);
        Assert.Equal(60, state.TraceIntervalSeconds);
        Assert.False(state.IsMonitoring);
    }
}
