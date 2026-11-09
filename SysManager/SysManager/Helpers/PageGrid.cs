// SysManager · PageGrid
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;

namespace SysManager.Helpers;

/// <summary>
/// The root grid of a page that stretches a row to the window: it keeps that row a floor, and says how tall the page
/// has to be for every row to get its own, for the shell's <see cref="PageFit"/> (#2704).
/// </summary>
/// <remarks>
/// A page is fixed rows over one or two star rows, a list, a console or a chart, which take what the page has left.
/// With nothing to stop them they got nothing once the fixed rows filled the page, and what was under them was cut
/// off the bottom: System Fixes' console had 0 px at the default window, with 186 px of the page past its edge.
/// <para>A star row that declares no <see cref="RowDefinition.MinHeight"/> gets <see cref="StretchFloor"/>, a list's
/// heading and a few rows; one that needs more, such as a chart, declares its own.</para>
/// <para><see cref="Required"/> is what the grid's own measure asked for, before <see cref="FrameworkElement"/> cuts
/// a desired size to the space offered: measured in less height than it needs, a grid still works out its fixed rows
/// and its floors, and only that cut hides the sum. <see cref="PageFit"/> reads it to choose between filling the
/// window and scrolling.</para>
/// </remarks>
public sealed class PageGrid : Grid
{
    /// <summary>The height a star row gets at the least, when it declares none of its own.</summary>
    public const double StretchFloor = 160;

    /// <summary>
    /// The height the page asked for in its last measure, its margin included: its fixed rows and its star rows'
    /// floors when the space offered was less than that, and the space offered when it was not.
    /// </summary>
    public double Required { get; private set; }

    /// <inheritdoc/>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        foreach (var row in RowDefinitions)
            if (row.Height.IsStar && row.MinHeight <= 0) row.MinHeight = StretchFloor;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size constraint)
    {
        var desired = base.MeasureOverride(constraint);
        Required = desired.Height + Margin.Top + Margin.Bottom;
        return desired;
    }
}
