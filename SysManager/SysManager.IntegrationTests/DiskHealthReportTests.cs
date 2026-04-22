// SysManager · DiskHealthReportTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.ComponentModel;
using SysManager.Models;

namespace SysManager.IntegrationTests;

public class DiskHealthReportTests
{
    [Fact]
    public void Defaults_AreSafe()
    {
        var r = new DiskHealthReport();
        Assert.Equal("", r.FriendlyName);
        Assert.Equal("", r.MediaType);
        Assert.Null(r.TemperatureC);
        Assert.Null(r.WearPercent);
        Assert.Null(r.PowerOnHours);
        Assert.Null(r.ReadErrors);
        Assert.Null(r.WriteErrors);
    }

    [Fact]
    public void PropertyChanged_FiresForAllFields()
    {
        var r = new DiskHealthReport();
        var raised = new HashSet<string>();
        ((INotifyPropertyChanged)r).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) raised.Add(e.PropertyName);
        };
        r.FriendlyName = "NVMe";
        r.MediaType = "SSD";
        r.BusType = "NVMe";
        r.SizeGB = 1000;
        r.HealthStatus = "Healthy";
        r.TemperatureC = 45;
        r.TemperatureMaxC = 80;
        r.WearPercent = 5;
        r.PowerOnHours = 3000;
        r.ReadErrors = 0;
        r.WriteErrors = 0;
        r.StartStopCount = 200;
        r.Verdict = "All good";
        r.VerdictColorHex = "#22C55E";

        Assert.Contains(nameof(r.FriendlyName), raised);
        Assert.Contains(nameof(r.MediaType), raised);
        Assert.Contains(nameof(r.BusType), raised);
        Assert.Contains(nameof(r.SizeGB), raised);
        Assert.Contains(nameof(r.HealthStatus), raised);
        Assert.Contains(nameof(r.TemperatureC), raised);
        Assert.Contains(nameof(r.TemperatureMaxC), raised);
        Assert.Contains(nameof(r.WearPercent), raised);
        Assert.Contains(nameof(r.PowerOnHours), raised);
        Assert.Contains(nameof(r.ReadErrors), raised);
        Assert.Contains(nameof(r.WriteErrors), raised);
        Assert.Contains(nameof(r.StartStopCount), raised);
        Assert.Contains(nameof(r.Verdict), raised);
        Assert.Contains(nameof(r.VerdictColorHex), raised);
    }
}
