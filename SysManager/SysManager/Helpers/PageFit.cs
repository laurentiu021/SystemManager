// SysManager · PageFit
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SysManager.Helpers;

/// <summary>
/// Shows the shell's page at the window's height when that is enough for it, and at the height its
/// <see cref="PageGrid"/> requires, a scroll bar away, when it is not (#2704).
/// </summary>
/// <remarks>
/// The content of a <see cref="ScrollViewer"/> with <see cref="ScrollViewer.CanContentScroll"/> on, so it is offered
/// the height the window shows rather than an endless one: a page measured in an endless height gives its lists all
/// the room their rows want, so they would make every row and scroll with the page. Here a page that fits fills the
/// window as before, and its lists keep their own scrolling; one that does not is laid out at its minimum, with every
/// star row at its floor, and scrolls as a whole.
/// <para>Vertical only. The page is always as wide as the window shows, as it was.</para>
/// </remarks>
public sealed class PageFit : Decorator, IScrollInfo
{
    /// <summary>How far one press of an arrow on the scroll bar moves, as a list's line.</summary>
    private const double Line = 16;

    /// <summary>A move shorter than this is none: a hundredth of a pixel.</summary>
    private const double NoMove = 0.01;

    private Size _extent;
    private Size _viewport;
    private double _offset;

    /// <inheritdoc/>
    public ScrollViewer? ScrollOwner { get; set; }

    /// <inheritdoc/>
    public bool CanVerticallyScroll { get; set; }

    /// <inheritdoc/>
    public bool CanHorizontallyScroll { get; set; }

    /// <inheritdoc/>
    public double ExtentWidth => _extent.Width;

    /// <inheritdoc/>
    public double ExtentHeight => _extent.Height;

    /// <inheritdoc/>
    public double ViewportWidth => _viewport.Width;

    /// <inheritdoc/>
    public double ViewportHeight => _viewport.Height;

    /// <inheritdoc/>
    public double HorizontalOffset => 0;

    /// <inheritdoc/>
    public double VerticalOffset => _offset;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size constraint)
    {
        var width = constraint.Width;
        // Endless only outside the shell's scroll viewer, which offers the height on screen: there the page is shown
        // at its minimum.
        var endless = double.IsInfinity(constraint.Height);
        var height = endless ? 0 : constraint.Height;
        var page = Child;
        if (page is null)
        {
            Update(new Size(width, height), new Size(width, height));
            return default;
        }

        page.Measure(new Size(width, height));
        var required = PageOf(page)?.Required ?? 0;
        var shown = Math.Max(height, required);
        if (shown > height) page.Measure(new Size(width, shown));
        Update(new Size(width, shown), new Size(width, height));
        return new Size(page.DesiredSize.Width, endless ? shown : height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Update(new Size(arrangeSize.Width, Math.Max(arrangeSize.Height, _extent.Height)), arrangeSize);
        Child?.Arrange(new Rect(0, -_offset, arrangeSize.Width, _extent.Height));
        return arrangeSize;
    }

    /// <summary>The page's root grid, under the content controls that hold it.</summary>
    private static PageGrid? PageOf(UIElement element)
    {
        object? node = element;
        while (node is ContentControl host) node = host.Content;
        return node as PageGrid;
    }

    private void Update(Size extent, Size viewport)
    {
        var changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        // A clamp only ever pulls the offset back, as the extent shrinks or the window grows.
        var offset = Clamp(_offset);
        if (offset < _offset)
        {
            _offset = offset;
            changed = true;
            InvalidateArrange();
        }

        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    private double Clamp(double offset) => Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));

    /// <inheritdoc/>
    public void SetVerticalOffset(double offset)
    {
        var clamped = Clamp(offset);
        if (Math.Abs(clamped - _offset) < NoMove) return;
        _offset = clamped;
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }

    // Across, there is nowhere to go: the page is always as wide as the window shows.

    /// <inheritdoc/>
    public void SetHorizontalOffset(double offset)
    {
    }

    /// <inheritdoc/>
    public void LineUp() => SetVerticalOffset(_offset - Line);

    /// <inheritdoc/>
    public void LineDown() => SetVerticalOffset(_offset + Line);

    /// <inheritdoc/>
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);

    /// <inheritdoc/>
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);

    /// <inheritdoc/>
    public void MouseWheelUp() => SetVerticalOffset(_offset - SystemParameters.WheelScrollLines * Line);

    /// <inheritdoc/>
    public void MouseWheelDown() => SetVerticalOffset(_offset + SystemParameters.WheelScrollLines * Line);

    /// <inheritdoc/>
    public void LineLeft()
    {
    }

    /// <inheritdoc/>
    public void LineRight()
    {
    }

    /// <inheritdoc/>
    public void PageLeft()
    {
    }

    /// <inheritdoc/>
    public void PageRight()
    {
    }

    /// <inheritdoc/>
    public void MouseWheelLeft()
    {
    }

    /// <inheritdoc/>
    public void MouseWheelRight()
    {
    }

    /// <inheritdoc/>
    /// <remarks>What keyboard focus asks for: a control tabbed to below the window is scrolled up to it.</remarks>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (Child is null || rectangle.IsEmpty || !IsAncestorOf(visual)) return Rect.Empty;
        var shown = visual.TransformToAncestor(this).TransformBounds(rectangle);
        var top = shown.Top + _offset;
        var target = _offset;
        if (top < _offset) target = top;
        else if (top + shown.Height > _offset + _viewport.Height)
            target = Math.Min(top, top + shown.Height - _viewport.Height);
        SetVerticalOffset(target);
        shown.Y = top - _offset;
        return shown;
    }
}
