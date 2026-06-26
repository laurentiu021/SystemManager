// SysManager · StandbyMemoryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class StandbyMemoryTests
{
    [Theory]
    [InlineData(512, 1024, true)]    // 512 MB available, threshold 1 GB → purge
    [InlineData(1023, 1024, true)]   // just below → purge
    [InlineData(1024, 1024, false)]  // exactly at threshold → no purge
    [InlineData(2048, 1024, false)]  // plenty available → no purge
    public void ShouldAutoPurge_FiresBelowThreshold(double availableMb, double thresholdMb, bool expected)
        => Assert.Equal(expected, StandbyMemoryViewModel.ShouldAutoPurge(availableMb, thresholdMb));

    [Fact]
    public void ShouldAutoPurge_GuardsZeroOrNegative()
    {
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(0, 1024));   // no reading → don't purge
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(512, 0));    // no threshold → don't purge
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(-1, 1024));
    }

    [Fact]
    public void MemoryStatus_FormatsAndComputesMb()
    {
        var s = new MemoryStatus(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75);
        Assert.Equal("16.0 GB", s.TotalDisplay);
        Assert.Equal("4.0 GB", s.AvailableDisplay);
        Assert.Equal("75%", s.LoadDisplay);
        Assert.Equal(4096, s.AvailableMb, 0);
    }

    [Fact]
    public void MemoryStatus_Empty_IsZero()
    {
        Assert.Equal(0UL, MemoryStatus.Empty.TotalBytes);
        Assert.Equal("0 B", MemoryStatus.Empty.AvailableDisplay);
    }
}
