// SysManager · WindowsThemeServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

public class WindowsThemeServiceTests
{
    private static TimeOnly T(int h, int m) => new(h, m);

    // Overnight window: dark 19:00 → light 07:00 (dark spans midnight).
    [Theory]
    [InlineData(19, 0, true)]    // exactly dark start → dark
    [InlineData(23, 30, true)]   // late evening → dark
    [InlineData(2, 0, true)]     // after midnight → still dark
    [InlineData(6, 59, true)]    // just before light → dark
    [InlineData(7, 0, false)]    // exactly light start → light
    [InlineData(7, 1, false)]    // morning → light
    [InlineData(12, 0, false)]   // midday → light
    [InlineData(18, 59, false)]  // just before dark → light
    public void ShouldBeDark_OvernightWindow(int h, int m, bool expectedDark)
        => Assert.Equal(expectedDark, WindowsThemeService.ShouldBeDark(T(h, m), T(19, 0), T(7, 0)));

    // Same-day window: dark 02:00 → light 07:00 (no wrap).
    [Theory]
    [InlineData(1, 59, false)]   // before dark → light
    [InlineData(2, 0, true)]     // exactly dark start → dark
    [InlineData(5, 0, true)]     // within window → dark
    [InlineData(7, 0, false)]    // exactly light start → light
    [InlineData(20, 0, false)]   // evening (outside same-day window) → light
    public void ShouldBeDark_SameDayWindow(int h, int m, bool expectedDark)
        => Assert.Equal(expectedDark, WindowsThemeService.ShouldBeDark(T(h, m), T(2, 0), T(7, 0)));

    [Fact]
    public void ShouldBeDark_EqualTimes_AlwaysLight()
    {
        // Degenerate/empty window → never auto-dark (no-op schedule).
        Assert.False(WindowsThemeService.ShouldBeDark(T(8, 0), T(8, 0), T(8, 0)));
        Assert.False(WindowsThemeService.ShouldBeDark(T(20, 0), T(8, 0), T(8, 0)));
    }
}
