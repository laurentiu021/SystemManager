// SysManager · MainWindowViewModelElevationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The window's badge and title say whether SysManager runs as administrator, decided here for each answer rather
/// than read from the PC running the test (#2742).
/// </summary>
/// <remarks>
/// The integration suite's check read the host's own answer inside an <c>if</c>, so it asserted the elevated title on
/// CI and the other one on a developer's PC, and neither on both.
/// </remarks>
public class MainWindowViewModelElevationTests
{
    [Theory]
    [InlineData(true, "Administrator", "SysManager — Administrator")]
    [InlineData(false, "Standard user", "SysManager")]
    public void TheBadgeAndTitle_SayHowItRuns(bool elevated, string badge, string title) =>
        Assert.Equal((badge, title), MainWindowViewModel.DescribeElevation(isElevated: elevated));
}
