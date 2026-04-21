using System.ComponentModel;
using SysManager.Models;

namespace SysManager.IntegrationTests;

public class PingTargetTests
{
    [Fact]
    public void DefaultCtor_HasSafeDefaults()
    {
        var t = new PingTarget();
        Assert.Equal("", t.Name);
        Assert.Equal("", t.Host);
        Assert.True(t.IsEnabled);
        Assert.False(t.IsCustom);
        Assert.Null(t.LastLatencyMs);
        Assert.Null(t.AverageMs);
        Assert.Equal(0, t.LossPercent);
        Assert.Equal("—", t.Status);
    }

    [Fact]
    public void ParamCtor_PopulatesFields()
    {
        var t = new PingTarget("Google", "8.8.8.8", "#FFFFFF", isCustom: true);
        Assert.Equal("Google", t.Name);
        Assert.Equal("8.8.8.8", t.Host);
        Assert.Equal("#FFFFFF", t.ColorHex);
        Assert.True(t.IsCustom);
    }

    [Fact]
    public void IsEnabled_RaisesPropertyChanged()
    {
        var t = new PingTarget("x", "1.1.1.1", "#000000");
        var raised = new List<string?>();
        ((INotifyPropertyChanged)t).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        t.IsEnabled = false;
        Assert.Contains(nameof(PingTarget.IsEnabled), raised);
    }

    [Fact]
    public void LastLatencyMs_AllowsNullAndValues()
    {
        var t = new PingTarget();
        t.LastLatencyMs = 42.5;
        Assert.Equal(42.5, t.LastLatencyMs);
        t.LastLatencyMs = null;
        Assert.Null(t.LastLatencyMs);
    }
}
