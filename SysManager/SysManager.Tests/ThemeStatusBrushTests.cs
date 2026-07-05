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

    // The WORST-CASE light card surface: the most-tinted layered surface across all light presets
    // (soft-blossom Surface2 = #FBCFE8). Small semantic/console text renders on the tinted Surface1/2
    // of the coloured presets, NOT on pure white — asserting only against #FFFFFF let sub-AA values
    // through on the pastel presets. This is the real worst case for the base + console tones.
    private static readonly Color LightTintedSurface = C("#FBCFE8");

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

    // Base semantic brushes (Info/Success/Warning/Danger) are used directly as small-text Foreground
    // across the app (e.g. Cleanup's TEMP-folders stat). They were static App.xaml resources that never
    // recomputed per mode, so their light-cyan/green/amber/red washed out on near-white light surfaces.
    // Now theme-derived — pin AA on white for light, legible on dark for dark.
    [Theory]
    [InlineData("Info")]
    [InlineData("Success")]
    [InlineData("Warning")]
    [InlineData("Danger")]
    public void LightPalette_BaseSemantic_MeetsWcagAaOnTintedSurface(string key)
    {
        // Assert against the most-tinted light surface (not just white) — that is where these tones
        // actually render as small text on the coloured presets, and where they previously dipped sub-AA.
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(false), key), LightTintedSurface);
        Assert.True(ratio >= 4.5,
            $"Light-theme base '{key}' brush must meet WCAG AA (4.5:1) as small text on the most-tinted light card surface; got {ratio:F2}:1.");
    }

    // Console output palette (Out*Brush) — same class: static-only before, so light-theme consoles
    // rendered near-invisible pale-grey body text on a near-white card. OutOutput/OutVerbose are neutral
    // text tones; the semantic console lines reuse the AA light tones. All must clear AA on white for light.
    [Theory]
    [InlineData("OutOutputBrush")]
    [InlineData("OutVerboseBrush")]
    [InlineData("OutInfoBrush")]
    [InlineData("OutWarnBrush")]
    [InlineData("OutErrorBrush")]
    [InlineData("OutDebugBrush")]
    [InlineData("OutProgressBrush")]
    public void LightPalette_ConsolePalette_MeetsWcagAaOnTintedSurface(string key)
    {
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(false), key), LightTintedSurface);
        Assert.True(ratio >= 4.5,
            $"Light-theme console '{key}' must meet WCAG AA (4.5:1) on the most-tinted light console card; got {ratio:F2}:1.");
    }

    [Fact]
    public void BaseAndConsole_LightAndDark_Diverge()
    {
        // Mode-awareness guard: if a base/console brush is identical in both modes it was never
        // recomputed and the light regression would silently return.
        foreach (var key in new[] { "Info", "Success", "Warning", "Danger", "OutOutputBrush", "OutInfoBrush" })
            Assert.NotEqual(Lookup(ThemeService.StatusPalette(true), key),
                            Lookup(ThemeService.StatusPalette(false), key));
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
