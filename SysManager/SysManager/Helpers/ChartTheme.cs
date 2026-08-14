// SysManager · ChartTheme — drives SkiaSharp chart paints from the active app theme
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SysManager.Services;
using WpfColor = System.Windows.Media.Color;

namespace SysManager.Helpers;

/// <summary>
/// Bridges the WPF <see cref="ThemeService"/> to the LiveCharts/SkiaSharp charts.
///
/// Chart labels, legends, and tooltips are painted with SkiaSharp <see cref="SolidColorPaint"/>
/// objects, which — unlike WPF <c>DynamicResource</c> brushes — do NOT track a theme change.
/// Historically the paints were hardcoded near-white (E6E9EE), so on any of the six light
/// presets the axis text/legend/tooltip rendered white-on-white and was invisible.
///
/// This helper mutates the <c>.Color</c> of the existing paint instances (no rebuild, so the
/// chart's bound Axis/paint references stay valid) using the current theme's foreground colors,
/// giving readable contrast on both light and dark presets. Callers apply it once at
/// construction and again on every <see cref="ThemeService.ThemeChanged"/>.
/// </summary>
internal static class ChartTheme
{
    /// <summary>WPF <see cref="WpfColor"/> → SkiaSharp <see cref="SKColor"/> (alpha preserved).</summary>
    public static SKColor Sk(WpfColor c) => new(c.R, c.G, c.B, c.A);

    /// <summary>
    /// The minimum contrast a chart line must keep against the card it is drawn on. WCAG 2.2 SC
    /// 1.4.11 (Non-text Contrast) asks 3:1 of a graphical object needed to understand the content,
    /// which a data series plainly is.
    /// </summary>
    private const double MinLineContrast = 3.0;

    /// <summary>The series stroke alpha used by every chart factory, needed to composite honestly.</summary>
    private const byte SeriesAlpha = 230;

    /// <summary>
    /// Returns <paramref name="seriesColor"/> darkened just enough to clear <see cref="MinLineContrast"/>
    /// against the current theme's card surface, or unchanged when it already does.
    /// <para>The series palette is tuned for the six dark presets, where every colour clears 4:1. On the
    /// six LIGHT presets those same tints wash out against a near-white card: measured across the three
    /// charts' 15 colour literals, 80 of 180 colour x preset combinations fell below 3:1 — every failure
    /// on a light preset, none on dark. The ping palette's #80FFDB measured 1.03:1, which renders as
    /// nothing at all.</para>
    /// <para>Only LIGHTNESS changes; the hue is preserved. Users learn "blue is CPU, purple is RAM", and
    /// a second hand-picked light-mode palette would both break that association and drift the moment a
    /// preset is added or retuned. Walking toward black until the contrast clears is arithmetic, so a
    /// 13th preset needs no new colours and dark presets are provably untouched (they already pass, so
    /// the loop exits on the first iteration).</para>
    /// </summary>
    public static SKColor Readable(SKColor seriesColor) =>
        ReadableAgainst(seriesColor, Sk(ThemeService.Instance.CurrentTheme.Surface));

    /// <summary>
    /// <see cref="Readable"/> against an explicit <paramref name="surface"/> rather than the live
    /// theme. Separate overload because the live-theme version can only ever be tested for the ONE
    /// preset that happens to be active, and switching presets in a test would persist to the user's
    /// real config file. This one is pure, so every preset can be checked in a single pass.
    /// </summary>
    public static SKColor ReadableAgainst(SKColor seriesColor, SKColor surface)
    {
        // Up to 80% toward black in 2% steps. 80% is the floor at which a hue is still recognisable;
        // every real preset clears the bar far earlier (the worst case needs 26%).
        for (var step = 0; step <= 40; step++)
        {
            var candidate = Mix(seriesColor, SKColors.Black, step * 0.02);
            if (Contrast(Mix(surface, candidate, SeriesAlpha / 255.0), surface) >= MinLineContrast)
                return candidate.WithAlpha(seriesColor.Alpha);
        }

        return Mix(seriesColor, SKColors.Black, 0.8).WithAlpha(seriesColor.Alpha);
    }

    /// <summary>Linear blend from <paramref name="from"/> toward <paramref name="to"/>. Alpha untouched.</summary>
    private static SKColor Mix(SKColor from, SKColor to, double amount) => new(
        (byte)Math.Round(from.Red + (to.Red - from.Red) * amount),
        (byte)Math.Round(from.Green + (to.Green - from.Green) * amount),
        (byte)Math.Round(from.Blue + (to.Blue - from.Blue) * amount));

    /// <summary>WCAG 2.x relative luminance.</summary>
    private static double Luminance(SKColor c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);
    }

    /// <summary>WCAG 2.x contrast ratio between two opaque colours.</summary>
    private static double Contrast(SKColor a, SKColor b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Repaints the supplied legend/tooltip paints and axes from the active theme. Separator
    /// lines use the theme border at low alpha so the gridlines stay subtle on any background.
    /// </summary>
    /// <param name="seriesBaseColors">
    /// Series paints keyed by their DESIGNED colour. Each paint is repainted to
    /// <see cref="Readable"/> of its base on every call, so switching from a light preset back to a
    /// dark one restores the original tint exactly. Deriving from the base rather than from the
    /// paint's current colour is what makes this idempotent — reading the live colour would darken
    /// an already-darkened line again on every theme change.
    /// </param>
    public static void Apply(
        SolidColorPaint legendText,
        SolidColorPaint tooltipText,
        SolidColorPaint tooltipBackground,
        IEnumerable<Axis> axes,
        IEnumerable<ISeries>? surfaceFilledSeries = null,
        IEnumerable<KeyValuePair<SKColor, SolidColorPaint>>? seriesBaseColors = null)
    {
        var t = ThemeService.Instance.CurrentTheme;
        var primary = Sk(t.TextPrimary);
        var secondary = Sk(t.TextSecondary);
        var separator = Sk(t.Border).WithAlpha(80);
        var surface = Sk(t.Surface);

        legendText.Color = primary;
        tooltipText.Color = primary;
        tooltipBackground.Color = Sk(t.Surface2);

        foreach (var axis in axes)
        {
            if (axis.LabelsPaint is SolidColorPaint labels) labels.Color = primary;
            if (axis.NamePaint is SolidColorPaint name) name.Color = secondary;
            if (axis.SeparatorsPaint is SolidColorPaint sep) sep.Color = separator;
        }

        // Series whose marker centre is meant to read as "the surface" (a hollow-looking dot ringed by
        // the series colour) — their GeometryFill is a surface tone that must invert with the theme, or
        // it stays dark on the light presets. Mutate .Color in place so the bound series stay valid.
        if (surfaceFilledSeries is not null)
            foreach (var s in surfaceFilledSeries)
                if (s is LineSeries<LiveChartsCore.Defaults.ObservablePoint> line
                    && line.GeometryFill is SolidColorPaint fill)
                    fill.Color = surface;

        // The lines themselves. Same in-place mutation as everything above, for the same reason: the
        // chart holds these paint instances, so replacing them would leave the visual bound to the old
        // one. See Readable for why only lightness moves.
        if (seriesBaseColors is not null)
            foreach (var (baseColor, paint) in seriesBaseColors)
                paint.Color = Readable(baseColor);
    }
}
