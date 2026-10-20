// SysManager · DisplayModeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class DisplayModeTests
{
    [Fact]
    public void Display_FormatsResolutionAndRefresh()
    {
        var m = new DisplayMode(2560, 1440, 165, 32);
        Assert.Equal("2560 × 1440 @ 165 Hz", m.Display);
        Assert.Equal("2560×1440", m.ResolutionDisplay);
        Assert.Equal("165 Hz", m.RefreshDisplay);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new DisplayMode(1920, 1080, 60, 32);
        var b = new DisplayMode(1920, 1080, 60, 32);
        var c = new DisplayMode(1920, 1080, 144, 32);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void TheSameModeAtAnotherBitDepth_IsTheSameMode()
    {
        // The mode list keeps the depth Windows listed first for each resolution and refresh rate, and the current
        // mode carries the depth in use. Apply never changes the depth, so the two are one mode and Apply must not be
        // offered for it.
        var listed = new DisplayMode(1920, 1080, 60, 16);
        var current = new DisplayMode(1920, 1080, 60, 32);

        Assert.Equal(listed, current);
        Assert.Equal(listed.GetHashCode(), current.GetHashCode());
        Assert.NotEqual(listed, new DisplayMode(1920, 1080, 75, 32));
        Assert.NotEqual(listed, new DisplayMode(1600, 1080, 60, 16));
    }

    [Theory]
    [InlineData(true, "Dell U2720Q (primary)")]
    [InlineData(false, "Dell U2720Q")]
    public void DisplayDevice_Display_TagsPrimary(bool primary, string expected)
    {
        var d = new DisplayDevice(@"\\.\DISPLAY1", "Dell U2720Q", primary, true);
        Assert.Equal(expected, d.Display);
    }
}
