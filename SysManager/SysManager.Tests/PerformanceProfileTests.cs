// SysManager · PerformanceProfileTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PerformanceProfile"/> model.
/// </summary>
public class PerformanceProfileTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var p = new PerformanceProfile();
        Assert.Equal("", p.ActivePlanName);
        Assert.Equal("", p.ActivePlanGuid);
        Assert.False(p.VisualEffectsReduced);
        Assert.False(p.GameModeEnabled);
        Assert.False(p.XboxGameBarDisabled);
        Assert.False(p.GpuMaxPerformance);
        Assert.False(p.HasNvidiaGpu);
        Assert.Equal("", p.NvidiaGpuName);
        Assert.False(p.ProcessorMaxState);
        Assert.Equal(0, p.ProcessorMinPercent);
    }

    [Fact]
    public void ProfileSummary_Balanced()
    {
        var p = new PerformanceProfile { ActivePlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e", ActivePlanName = "Balanced" };
        Assert.Equal("Balanced", p.ProfileSummary);
    }

    [Fact]
    public void ProfileSummary_HighPerformance()
    {
        var p = new PerformanceProfile { ActivePlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", ActivePlanName = "High performance" };
        Assert.Equal("High Performance", p.ProfileSummary);
    }

    [Fact]
    public void ProfileSummary_UltimatePerformance()
    {
        var p = new PerformanceProfile { ActivePlanGuid = "custom-guid", ActivePlanName = "Ultimate Performance" };
        Assert.Equal("Ultimate Performance", p.ProfileSummary);
    }

    [Fact]
    public void ProfileSummary_CustomPlan_ReturnsName()
    {
        var p = new PerformanceProfile { ActivePlanGuid = "custom-guid", ActivePlanName = "My Custom Plan" };
        Assert.Equal("My Custom Plan", p.ProfileSummary);
    }

    [Fact]
    public void PropertyChange_Notifies()
    {
        var p = new PerformanceProfile();
        var changed = new List<string>();
        p.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        p.ActivePlanName = "Test";
        p.VisualEffectsReduced = true;
        p.GameModeEnabled = true;
        p.XboxGameBarDisabled = true;
        p.GpuMaxPerformance = true;
        p.HasNvidiaGpu = true;
        p.ProcessorMaxState = true;
        p.ProcessorMinPercent = 100;

        Assert.Contains("ActivePlanName", changed);
        Assert.Contains("VisualEffectsReduced", changed);
        Assert.Contains("GameModeEnabled", changed);
        Assert.Contains("XboxGameBarDisabled", changed);
        Assert.Contains("GpuMaxPerformance", changed);
        Assert.Contains("HasNvidiaGpu", changed);
        Assert.Contains("ProcessorMaxState", changed);
        Assert.Contains("ProcessorMinPercent", changed);
    }
}
