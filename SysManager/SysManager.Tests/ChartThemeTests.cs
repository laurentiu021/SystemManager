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

    /// <summary>
    /// Every chart series colour must clear 3:1 against every preset's card surface.
    /// <para>The series palette was tuned for the six dark presets, where all 13 literals across the
    /// three charts clear 4:1. On the six LIGHT presets the same tints wash out against a near-white
    /// card: measured before the fix, 80 of 180 colour x preset combinations fell below WCAG 1.4.11's
    /// 3:1 for a non-text graphical object, and every failure was on a light preset. The ping palette's
    /// #80FFDB measured 1.03:1 — visually identical to not drawing the line at all.</para>
    /// <para>Driven from <see cref="ThemePreset.Defaults"/> rather than a copied list, so a 13th preset
    /// is covered automatically. Measures <see cref="ChartTheme.ReadableAgainst"/> against each preset's
    /// own surface WITHOUT switching the live theme — <c>ThemeService.SetPreset</c> persists to the
    /// user's real config file, which a test must never touch.</para>
    /// </summary>
    [Fact]
    public void EverySeriesColour_ClearsThreeToOne_OnEveryPreset()
    {
        // The designed palette, exactly as the three chart view models declare it.
        string[] palette =
        [
            "#60A5FA", "#A78BFA", "#34D399", "#F59E0B", "#EF4444",   // ResourceHistory + Bandwidth
            "#4CC9F0", "#80FFDB", "#F72585", "#FFD166",              // ping palette 1-4
            "#B388FF", "#06D6A0", "#FF6B6B", "#F8961E",              // ping palette 5-8
        ];

        Assert.Equal(12, ThemePreset.Defaults.Count);

        var offenders = new List<string>();
        var checkedPairs = 0;

        foreach (var (id, preset) in ThemePreset.Defaults)
        {
            var surface = ChartTheme.Sk(preset.Surface);
            foreach (var hex in palette)
            {
                checkedPairs++;
                var designed = SKColor.Parse(hex.TrimStart('#')).WithAlpha(230);
                var readable = ChartTheme.ReadableAgainst(designed, surface);
                var ratio = ContrastRatio(Composite(readable, surface), surface);
                if (ratio < 3.0)
                    offenders.Add($"{id} / {hex} -> {ratio:F2}:1");
            }
        }

        // Vacuity floor: 12 presets x 13 colours. A parse or enumeration fault would otherwise let an
        // empty sweep report success.
        Assert.Equal(12 * palette.Length, checkedPairs);

        Assert.True(offenders.Count == 0,
            "these chart series colours are below 3:1 against their preset's card surface, so the line "
            + $"is not distinguishable from the background (WCAG 1.4.11):\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// The adjustment must be a no-op on the dark presets, and it must be idempotent.
    /// <para>Both properties are what make the rule safe to run on every theme change: dark themes
    /// already pass, so their identity colours must come back byte-identical; and re-deriving from the
    /// DESIGNED colour rather than the current one is what stops a light -> dark -> light cycle from
    /// darkening an already-darkened line into mud.</para>
    /// </summary>
    [Fact]
    public void Readable_LeavesDarkPresetsUntouched_AndIsIdempotent()
    {
        var designed = SKColor.Parse("34D399").WithAlpha(230);   // the worst light-preset offender
        var darkSurface = ChartTheme.Sk(ThemePreset.Defaults["midnight-indigo"].Surface);
        var lightSurface = ChartTheme.Sk(ThemePreset.Defaults["clean-indigo"].Surface);

        // Dark: unchanged, alpha included.
        Assert.Equal(designed, ChartTheme.ReadableAgainst(designed, darkSurface));

        // Light: changed, but still the same hue family and still at the series alpha.
        var adjusted = ChartTheme.ReadableAgainst(designed, lightSurface);
        Assert.NotEqual(designed, adjusted);
        Assert.Equal(designed.Alpha, adjusted.Alpha);
        Assert.True(adjusted.Green > adjusted.Red && adjusted.Green > adjusted.Blue,
            $"the green series must stay green-dominant; got R{adjusted.Red} G{adjusted.Green} B{adjusted.Blue}");

        // Idempotent from the base: every ThemeChanged re-derives from the designed colour, so the same
        // inputs must always give the same output.
        Assert.Equal(adjusted, ChartTheme.ReadableAgainst(designed, lightSurface));
    }

    /// <summary>
    /// <see cref="ChartTheme.Apply"/> must derive each stroke from its REGISTERED base colour, never from
    /// the colour the paint currently holds.
    /// <para>Every theme change calls Apply, and a user can switch presets any number of times. Feeding
    /// the live colour back in would darken an already-darkened line on each pass, fading the chart
    /// toward black over a session — and a light → dark switch would never restore the designed tint.
    /// The two tests above cannot catch that: they call <c>ReadableAgainst</c> directly and never
    /// exercise Apply's plumbing.</para>
    /// <para>Proved without switching the live theme (<c>ThemeService.SetPreset</c> writes the user's real
    /// config). The paint starts at a sentinel that differs from its registered base, so a live-colour
    /// derivation lands on the sentinel on ANY preset — including the dark ones where the adjustment is
    /// an identity function and a same-colour test would pass vacuously.</para>
    /// </summary>
    [Fact]
    public void Apply_DerivesEachStrokeFromItsBaseColour_NotTheLiveOne()
    {
        var designed = SKColor.Parse("34D399").WithAlpha(230);
        var sentinel = SKColors.Magenta.WithAlpha(230);
        var stroke = new SolidColorPaint(sentinel, 2);
        List<KeyValuePair<SKColor, SolidColorPaint>> registered = [new(designed, stroke)];

        var liveSurface = ChartTheme.Sk(ThemeService.Instance.CurrentTheme.Surface);
        var expected = ChartTheme.ReadableAgainst(designed, liveSurface);
        Assert.NotEqual(sentinel, expected);   // vacuity floor: the sentinel must be distinguishable

        void Repaint() => ChartTheme.Apply(
            new SolidColorPaint(SKColors.Black), new SolidColorPaint(SKColors.Black),
            new SolidColorPaint(SKColors.Black), [new Axis()],
            seriesBaseColors: registered);

        Repaint();
        Assert.Equal(expected, stroke.Color);

        // Ten more passes, as a user flipping presets repeatedly would trigger: still the base-derived
        // colour, never a re-darkened one.
        for (var i = 0; i < 10; i++) Repaint();

        Assert.Equal(expected, stroke.Color);
        Assert.Equal(designed.Alpha, stroke.Color.Alpha);
    }

    /// <summary>Composites a series stroke over its surface at the alpha the charts actually use.</summary>
    private static SKColor Composite(SKColor line, SKColor surface)
    {
        var a = line.Alpha / 255.0;
        return new SKColor(
            (byte)Math.Round(surface.Red + (line.Red - surface.Red) * a),
            (byte)Math.Round(surface.Green + (line.Green - surface.Green) * a),
            (byte)Math.Round(surface.Blue + (line.Blue - surface.Blue) * a));
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
