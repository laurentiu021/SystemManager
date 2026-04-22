// SysManager · PingTargetTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PingTarget"/> — observable model used by the network monitor.
/// </summary>
public class PingTargetTests
{
    [Fact]
    public void DefaultConstructor_SetsDefaults()
    {
        var t = new PingTarget();
        Assert.Equal("", t.Name);
        Assert.Equal("", t.Host);
        Assert.True(t.IsEnabled);
        Assert.False(t.IsCustom);
        Assert.Null(t.LastLatencyMs);
        Assert.Null(t.AverageMs);
        Assert.Null(t.JitterMs);
        Assert.Equal(0, t.LossPercent);
        Assert.Equal("—", t.Status);
        Assert.Equal("#4CC9F0", t.ColorHex);
        Assert.Equal(TargetRole.Generic, t.Role);
    }

    [Fact]
    public void ParameterizedConstructor_SetsValues()
    {
        var t = new PingTarget("Google DNS", "8.8.8.8", "#FF0000", true, TargetRole.PublicDns);
        Assert.Equal("Google DNS", t.Name);
        Assert.Equal("8.8.8.8", t.Host);
        Assert.Equal("#FF0000", t.ColorHex);
        Assert.True(t.IsCustom);
        Assert.Equal(TargetRole.PublicDns, t.Role);
    }

    [Fact]
    public void PropertyChanged_FiresOnNameChange()
    {
        var t = new PingTarget();
        var changed = new List<string>();
        t.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        t.Name = "Test";
        Assert.Contains("Name", changed);
    }

    [Fact]
    public void PropertyChanged_FiresOnLatencyChange()
    {
        var t = new PingTarget();
        var changed = new List<string>();
        t.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        t.LastLatencyMs = 15.5;
        Assert.Contains("LastLatencyMs", changed);
    }

    [Fact]
    public void PropertyChanged_FiresOnStatusChange()
    {
        var t = new PingTarget();
        var changed = new List<string>();
        t.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        t.Status = "OK";
        Assert.Contains("Status", changed);
    }

    [Fact]
    public void IsEnabled_CanBeToggled()
    {
        var t = new PingTarget();
        Assert.True(t.IsEnabled);
        t.IsEnabled = false;
        Assert.False(t.IsEnabled);
    }

    [Theory]
    [InlineData(TargetRole.Generic)]
    [InlineData(TargetRole.Gateway)]
    [InlineData(TargetRole.PublicDns)]
    [InlineData(TargetRole.GameServer)]
    [InlineData(TargetRole.Streaming)]
    public void Role_CanBeSet(TargetRole role)
    {
        var t = new PingTarget { Role = role };
        Assert.Equal(role, t.Role);
    }

    [Fact]
    public void Stats_CanBeUpdated()
    {
        var t = new PingTarget();
        t.AverageMs = 25.3;
        t.JitterMs = 2.1;
        t.LossPercent = 5.0;
        Assert.Equal(25.3, t.AverageMs);
        Assert.Equal(2.1, t.JitterMs);
        Assert.Equal(5.0, t.LossPercent);
    }
}
