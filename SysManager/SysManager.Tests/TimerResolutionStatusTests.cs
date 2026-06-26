// SysManager · TimerResolutionStatusTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class TimerResolutionStatusTests
{
    [Theory]
    [InlineData(5000u, 0.5)]
    [InlineData(10000u, 1.0)]
    [InlineData(156250u, 15.625)]
    [InlineData(0u, 0.0)]
    public void ToMilliseconds_Converts100NsUnits(uint hundredNs, double expectedMs)
        => Assert.Equal(expectedMs, TimerResolutionStatus.ToMilliseconds(hundredNs), 3);

    [Fact]
    public void DisplayProperties_FormatMilliseconds()
    {
        var s = new TimerResolutionStatus(5000, 156250, 5000, true);
        Assert.Equal("0.5 ms", s.FinestDisplay);
        Assert.Equal("15.625 ms", s.CoarsestDisplay);
        Assert.Equal("0.5 ms", s.CurrentDisplay);
    }

    [Fact]
    public void IsHighResolution_TrueWhenCurrentNearFinest()
    {
        // Current exactly at the finest achievable → high resolution.
        var s = new TimerResolutionStatus(5000, 156250, 5000, true);
        Assert.True(s.IsHighResolution);
    }

    [Fact]
    public void IsHighResolution_TrueWithinTolerance()
    {
        // The effective value can land a few units off the requested one.
        var s = new TimerResolutionStatus(5000, 156250, 5400, true);
        Assert.True(s.IsHighResolution);
    }

    [Fact]
    public void IsHighResolution_FalseAtDefaultCoarseTimer()
    {
        // Sitting at the ~15.6 ms Windows default → not high resolution.
        var s = new TimerResolutionStatus(5000, 156250, 156250, false);
        Assert.False(s.IsHighResolution);
    }

    [Fact]
    public void FormatMs_TrimsTrailingZeros()
    {
        Assert.Equal("1 ms", TimerResolutionStatus.FormatMs(1.0));
        Assert.Equal("0.5 ms", TimerResolutionStatus.FormatMs(0.5));
        Assert.Equal("15.625 ms", TimerResolutionStatus.FormatMs(15.625));
    }
}
