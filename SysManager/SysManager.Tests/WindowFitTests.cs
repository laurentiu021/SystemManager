// SysManager · WindowFitTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// <see cref="WindowFit"/> keeps the window on its screen: where it is when it fits, cut to the work area and centred
/// when it does not, and maximized on a screen smaller than its minimum size (#2704).
/// </summary>
/// <remarks>
/// All in a screen's pixels, as the window reads them: the app's 1280 by 820 and its 960 by 640 minimum, times the
/// screen's scale, on work areas of the height a taskbar leaves.
/// </remarks>
public class WindowFitTests
{
    private static readonly Size Minimum = new(960, 640);

    [Fact]
    public void AWindowThatFits_StaysWhereWindowsPutIt()
    {
        var window = new Rect(104, 104, 1280, 820);

        var placement = WindowFit.Place(window, Minimum, new Rect(0, 0, 2560, 1392));

        Assert.Null(placement.Bounds);
        Assert.Equal(Minimum, placement.Minimum);
        Assert.False(placement.Maximize);
    }

    [Fact]
    public void ATallWindowOnA1366By768Screen_IsCutToTheWorkArea_AndCentredInIt()
    {
        var placement = WindowFit.Place(new Rect(26, 26, 1280, 820), Minimum, new Rect(0, 0, 1366, 728));

        Assert.Equal(new Rect(43, 0, 1280, 728), placement.Bounds);
        Assert.Equal(Minimum, placement.Minimum);
        Assert.False(placement.Maximize);
    }

    [Fact]
    public void TheWindowOnA1920By1080ScreenAt150Percent_TakesTheWholeWorkArea()
    {
        var placement = WindowFit.Place(new Rect(39, 39, 1920, 1230), new Size(1440, 960), new Rect(0, 0, 1920, 1032));

        Assert.Equal(new Rect(0, 0, 1920, 1032), placement.Bounds);
        Assert.Equal(new Size(1440, 960), placement.Minimum);
        Assert.False(placement.Maximize);
    }

    [Fact]
    public void AScreenShorterThanTheMinimumSize_LowersTheMinimumToIt_AndOpensMaximized()
    {
        // 1366 x 768 at 125%: the 960 x 640 minimum is 1200 x 800 pixels, and the work area 720 tall.
        var placement = WindowFit.Place(new Rect(32, 32, 1600, 1025), new Size(1200, 800), new Rect(0, 0, 1366, 720));

        Assert.Equal(new Rect(0, 0, 1366, 720), placement.Bounds);
        Assert.Equal(new Size(1200, 720), placement.Minimum);
        Assert.True(placement.Maximize);
    }

    [Fact]
    public void AWindowOnASecondScreen_IsCentredInThatScreensWorkArea()
    {
        var placement = WindowFit.Place(new Rect(1946, 26, 1280, 820), Minimum, new Rect(1920, 0, 1366, 728));

        Assert.Equal(new Rect(1963, 0, 1280, 728), placement.Bounds);
        Assert.False(placement.Maximize);
    }

    [Fact]
    public void AWindowThatFitsTheScreen_ButHangsOverItsEdge_IsBroughtBackOntoIt()
    {
        var placement = WindowFit.Place(new Rect(2000, 900, 1280, 820), Minimum, new Rect(0, 0, 2560, 1392));

        Assert.Equal(new Rect(640, 286, 1280, 820), placement.Bounds);
        Assert.Equal(Minimum, placement.Minimum);
        Assert.False(placement.Maximize);
    }
}
