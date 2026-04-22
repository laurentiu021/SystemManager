// SysManager · HealthDiagnosticTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.ComponentModel;
using SysManager.Models;

namespace SysManager.IntegrationTests;

public class HealthDiagnosticTests
{
    [Fact]
    public void Defaults_AreSafe()
    {
        var d = new HealthDiagnostic();
        Assert.Equal(HealthVerdict.Unknown, d.Verdict);
        Assert.Equal("Waiting for data…", d.Headline);
        Assert.Equal("", d.Detail);
        Assert.Equal("#9AA0A6", d.ColorHex);
        Assert.Equal(0, d.WorstLossPercent);
        Assert.Equal(0, d.WorstJitterMs);
        Assert.Equal(0, d.AveragePingMs);
    }

    [Fact]
    public void PropertyChanged_FiresForAllFields()
    {
        var d = new HealthDiagnostic();
        var raised = new HashSet<string>();
        ((INotifyPropertyChanged)d).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) raised.Add(e.PropertyName);
        };
        d.Verdict = HealthVerdict.Good;
        d.Headline = "Healthy";
        d.Detail = "All good";
        d.ColorHex = "#06D6A0";
        d.WorstLossPercent = 1;
        d.WorstJitterMs = 2;
        d.AveragePingMs = 3;
        Assert.Contains(nameof(d.Verdict), raised);
        Assert.Contains(nameof(d.Headline), raised);
        Assert.Contains(nameof(d.Detail), raised);
        Assert.Contains(nameof(d.ColorHex), raised);
        Assert.Contains(nameof(d.WorstLossPercent), raised);
        Assert.Contains(nameof(d.WorstJitterMs), raised);
        Assert.Contains(nameof(d.AveragePingMs), raised);
    }
}
