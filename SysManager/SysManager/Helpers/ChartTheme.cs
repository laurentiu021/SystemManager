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
    /// Repaints the supplied legend/tooltip paints and axes from the active theme. Separator
    /// lines use the theme border at low alpha so the gridlines stay subtle on any background.
    /// </summary>
    public static void Apply(
        SolidColorPaint legendText,
        SolidColorPaint tooltipText,
        SolidColorPaint tooltipBackground,
        IEnumerable<Axis> axes,
        IEnumerable<ISeries>? surfaceFilledSeries = null)
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
    }
}
