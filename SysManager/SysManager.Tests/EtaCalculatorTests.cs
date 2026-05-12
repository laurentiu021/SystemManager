// SysManager · EtaCalculatorTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

public class EtaCalculatorTests
{
    [Fact]
    public void Reset_ClearsState()
    {
        var eta = new EtaCalculator();
        eta.Reset();
        Assert.Null(eta.Remaining);
        Assert.Equal(string.Empty, eta.RemainingText);
    }

    [Fact]
    public void Update_AtZeroPercent_ReturnsCalculating()
    {
        var eta = new EtaCalculator();
        eta.Reset();
        var text = eta.Update(0);
        Assert.Equal("calculating…", text);
        Assert.Null(eta.Remaining);
    }

    [Fact]
    public void Update_At100Percent_ReturnsDone()
    {
        var eta = new EtaCalculator();
        eta.Reset();
        var text = eta.Update(100);
        Assert.Equal("done", text);
        Assert.Equal(TimeSpan.Zero, eta.Remaining);
    }

    [Fact]
    public void Update_AtMidProgress_ReturnsEstimate()
    {
        var eta = new EtaCalculator();
        eta.Reset();
        // Simulate some elapsed time by updating at 50%
        Thread.Sleep(100);
        var text = eta.Update(50);
        Assert.NotEqual(string.Empty, text);
        Assert.NotNull(eta.Remaining);
        Assert.True(eta.Remaining >= TimeSpan.Zero);
    }

    [Fact]
    public void Update_ClampsAbove100()
    {
        var eta = new EtaCalculator();
        eta.Reset();
        var text = eta.Update(150);
        Assert.Equal("done", text);
    }

    [Fact]
    public void Update_ClampsBelow0()
    {
        var eta = new EtaCalculator();
        eta.Reset();
        var text = eta.Update(-5);
        Assert.Equal("calculating…", text);
    }

    [Theory]
    [InlineData(3, "a few seconds")]
    [InlineData(30, "~30 s")]
    [InlineData(90, "~1 min 30 s")]
    [InlineData(3600, "~1 h")]
    [InlineData(3660, "~1 h 1 min")]
    public void FormatTimeSpan_FormatsCorrectly(int seconds, string expected)
    {
        var result = EtaCalculator.FormatTimeSpan(TimeSpan.FromSeconds(seconds));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Update_WithoutReset_ReturnsCalculating()
    {
        var eta = new EtaCalculator();
        // No Reset() called — stopwatch not running
        var text = eta.Update(50);
        Assert.Equal("calculating…", text);
    }

    [Fact]
    public void MultipleResets_WorkCorrectly()
    {
        var eta = new EtaCalculator();
        eta.Reset();
        Thread.Sleep(50);
        eta.Update(50);
        Assert.NotNull(eta.Remaining);

        eta.Reset();
        Assert.Null(eta.Remaining);
        Assert.Equal(string.Empty, eta.RemainingText);
    }
}
