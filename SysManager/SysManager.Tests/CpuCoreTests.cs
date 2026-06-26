// SysManager · CpuCoreTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class CpuCoreTests
{
    [Fact]
    public void Display_UsesLogicalIndex()
        => Assert.Equal("CPU 5", new CpuCore(5, 1, "Performance").Display);

    [Theory]
    [InlineData("Performance", true, false)]
    [InlineData("Efficiency", false, true)]
    [InlineData("Standard", false, false)]
    public void TypeFlags_MatchCoreType(string type, bool perf, bool eff)
    {
        var c = new CpuCore(0, 0, type);
        Assert.Equal(perf, c.IsPerformance);
        Assert.Equal(eff, c.IsEfficiency);
    }
}
