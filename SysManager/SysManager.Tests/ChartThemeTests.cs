// SysManager · ChartThemeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Regression tests for <see cref="ChartTheme"/>. The bug: chart axis/legend/tooltip paints
/// were hardcoded near-white (E6E9EE) and set once, so on the six light presets the text
/// rendered white-on-white and was invisible (measured 1.1–1.2:1 contrast). ChartTheme drives
/// those paints from the active theme's foreground colors instead. These tests pin that
/// <see cref="ChartTheme.Apply"/> repaints the supplied paints/axes to the current theme.
/// </summary>
public class ChartThemeTests
{
    [Fact]
    public void Sk_PreservesRgbaChannels()
    {
        var c = System.Windows.Media.Color.FromArgb(0x12, 0x34, 0x56, 0x78);
        var sk = ChartTheme.Sk(c);
        Assert.Equal(0x34, sk.Red);
        Assert.Equal(0x56, sk.Green);
        Assert.Equal(0x78, sk.Blue);
        Assert.Equal(0x12, sk.Alpha);
    }

    [Fact]
    public void Apply_RepaintsTextToThemeForeground_NotHardcodedWhite()
    {
        // Start every paint at a deliberately-wrong color so we can prove Apply overwrote it.
        var legend = new SolidColorPaint(SKColors.Magenta);
        var tooltipText = new SolidColorPaint(SKColors.Magenta);
        var tooltipBg = new SolidColorPaint(SKColors.Magenta);
        var axis = new Axis
        {
            LabelsPaint = new SolidColorPaint(SKColors.Magenta),
            NamePaint = new SolidColorPaint(SKColors.Magenta),
            SeparatorsPaint = new SolidColorPaint(SKColors.Magenta)
        };

        ChartTheme.Apply(legend, tooltipText, tooltipBg, [axis]);

        var t = ThemeService.Instance.CurrentTheme;
        Assert.Equal(ChartTheme.Sk(t.TextPrimary), legend.Color);
        Assert.Equal(ChartTheme.Sk(t.TextPrimary), tooltipText.Color);
        Assert.Equal(ChartTheme.Sk(t.Surface2), tooltipBg.Color);
        Assert.Equal(ChartTheme.Sk(t.TextPrimary), ((SolidColorPaint)axis.LabelsPaint!).Color);
        Assert.Equal(ChartTheme.Sk(t.TextSecondary), ((SolidColorPaint)axis.NamePaint!).Color);

        // None of the repainted foregrounds may remain the sentinel magenta.
        Assert.NotEqual(SKColors.Magenta, legend.Color);
        Assert.NotEqual(SKColors.Magenta, ((SolidColorPaint)axis.LabelsPaint!).Color);
    }

    [Fact]
    public void Apply_LabelContrastsAgainstBackground_OnCurrentTheme()
    {
        // The point of the fix: label text must be readable against the theme background.
        // Assert a real luminance-contrast gap (not white-on-white) for the active theme.
        var axis = new Axis { LabelsPaint = new SolidColorPaint(SKColors.Magenta) };
        ChartTheme.Apply(new SolidColorPaint(SKColors.Black), new SolidColorPaint(SKColors.Black),
            new SolidColorPaint(SKColors.Black), [axis]);

        var t = ThemeService.Instance.CurrentTheme;
        var text = ChartTheme.Sk(t.TextPrimary);
        var bg = ChartTheme.Sk(t.Background);
        Assert.True(ContrastRatio(text, bg) >= 4.5,
            $"Chart label text must meet WCAG 4.5:1 against the theme background; got {ContrastRatio(text, bg):F2}:1.");
    }

    private static double ContrastRatio(SKColor a, SKColor b)
    {
        double la = RelLum(a), lb = RelLum(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelLum(SKColor c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.Red) + 0.7152 * Ch(c.Green) + 0.0722 * Ch(c.Blue);
    }
}
