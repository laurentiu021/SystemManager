// SysManager · ThemeStatusBrushTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Regression tests for <see cref="ThemeService.StatusPalette"/>. The bug: the semantic status
/// brushes (WarningText #FCD34D, SuccessText, InfoText, DangerText) were static in App.xaml and
/// calibrated for dark surfaces; <see cref="ThemeService.Apply"/> repainted Surface/Text/Accent per
/// preset but NEVER these, so on the six LIGHT presets the pale warning/info text rendered on a
/// near-white banner and failed WCAG contrast (e.g. #FCD34D on #FFFFFF ≈ 1.4:1 — illegible).
/// These tests pin that the light palette uses dark, saturated text that meets WCAG AA (4.5:1),
/// while the dark palette stays legible on dark surfaces. They FAIL against the old static colors.
/// </summary>
public class ThemeStatusBrushTests
{
    // The brightest light-preset surface a status banner layers over (clean-indigo Surface = #FFFFFF)
    // and the darkest dark-preset base (midnight-indigo Background = #070A0F). Text must contrast
    // against the worst-case surface for its mode.
    private static readonly Color LightSurface = C("#FFFFFF");
    private static readonly Color DarkSurface = C("#070A0F");

    [Theory]
    [InlineData("WarningText")]
    [InlineData("SuccessText")]
    [InlineData("InfoText")]
    [InlineData("DangerText")]
    public void LightPalette_StatusText_MeetsWcagAaOnWhite(string key)
    {
        var color = Lookup(ThemeService.StatusPalette(isDark: false), key);
        var ratio = ContrastRatio(color, LightSurface);
        Assert.True(ratio >= 4.5,
            $"Light-theme {key} must meet WCAG AA (4.5:1) against the near-white banner surface; got {ratio:F2}:1.");
    }

    [Theory]
    [InlineData("WarningText")]
    [InlineData("SuccessText")]
    [InlineData("InfoText")]
    [InlineData("DangerText")]
    public void DarkPalette_StatusText_StaysLegibleOnDark(string key)
    {
        var color = Lookup(ThemeService.StatusPalette(isDark: true), key);
        var ratio = ContrastRatio(color, DarkSurface);
        Assert.True(ratio >= 4.5,
            $"Dark-theme {key} must stay legible (4.5:1) on the dark surface; got {ratio:F2}:1.");
    }

    [Fact]
    public void LightAndDark_ProduceDifferentWarningText()
    {
        // The whole point of the fix: the two modes must diverge. If they're equal, the palette was
        // never actually mode-aware and the light-theme regression would silently return.
        var light = Lookup(ThemeService.StatusPalette(false), "WarningText");
        var dark = Lookup(ThemeService.StatusPalette(true), "WarningText");
        Assert.NotEqual(dark, light);
    }

    [Fact]
    public void Palette_CoversAllFourStatusTextKeys_InBothModes()
    {
        foreach (var mode in new[] { true, false })
        {
            var keys = ThemeService.StatusPalette(mode).Select(p => p.Key).ToHashSet();
            Assert.Contains("WarningText", keys);
            Assert.Contains("SuccessText", keys);
            Assert.Contains("InfoText", keys);
            Assert.Contains("DangerText", keys);
        }
    }

    private static Color Lookup(IReadOnlyList<(string Key, Color Color)> palette, string key)
        => palette.First(p => p.Key == key).Color;

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelLum(a), lb = RelLum(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelLum(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }
}
