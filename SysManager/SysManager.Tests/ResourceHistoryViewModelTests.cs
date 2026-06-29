// SysManager · ResourceHistoryViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class ResourceHistoryViewModelTests
{
    private static ResourceSample Sample(double cpu, double ram, double? cpuTemp = null)
        => new(new DateTime(2026, 6, 29), cpu, ram, null, cpuTemp, null);

    [Fact]
    public void BuildSummary_EmptySamples_IsEmpty()
        => Assert.Equal("", ResourceHistoryViewModel.BuildSummary([]));

    [Fact]
    public void BuildSummary_ReportsCpuAndRamAveragesAndPeaks()
    {
        var samples = new[] { Sample(20, 40), Sample(60, 80) };
        var summary = ResourceHistoryViewModel.BuildSummary(samples);
        Assert.Contains("CPU avg 40%", summary);
        Assert.Contains("peak 60%", summary);
        Assert.Contains("RAM avg 60%", summary);
        Assert.Contains("peak 80%", summary);
    }

    [Fact]
    public void BuildSummary_WithoutTemps_OmitsTempSegment()
    {
        var summary = ResourceHistoryViewModel.BuildSummary([Sample(10, 10)]);
        Assert.DoesNotContain("temp", summary);
    }

    [Fact]
    public void BuildSummary_WithTemps_IncludesPeakTemp()
    {
        var samples = new[] { Sample(10, 10, cpuTemp: 55), Sample(10, 10, cpuTemp: 72) };
        var summary = ResourceHistoryViewModel.BuildSummary(samples);
        Assert.Contains("CPU temp peak 72°C", summary);
    }
}
