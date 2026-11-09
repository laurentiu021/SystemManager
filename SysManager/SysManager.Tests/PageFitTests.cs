// SysManager · PageFitTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// <see cref="PageFit"/> gives the shell's page the window's height when that is enough, and the height its
/// <see cref="PageGrid"/> requires, scrolled, when not (#2704).
/// </summary>
/// <remarks>
/// Laid out without a window or a scroll viewer: what the scroll viewer would read is the extent, the viewport and the
/// offset, and those are what is pinned.
/// </remarks>
public class PageFitTests
{
    /// <summary>Fixed rows of 300, 300 and 300 over a list, the last fixed row ending in a button.</summary>
    private static (PageFit Fit, PageGrid Page, Button Bottom, Button Top) Shell()
    {
        var page = new PageGrid();
        page.BeginInit();
        for (var r = 0; r < 3; r++)
        {
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var fixedRow = new Border { Height = 300 };
            Grid.SetRow(fixedRow, r);
            page.Children.Add(fixedRow);
        }
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var list = new ScrollViewer { Content = new Border { Height = 2000 } };
        Grid.SetRow(list, 3);
        page.Children.Add(list);
        var top = new Button { Height = 30, VerticalAlignment = VerticalAlignment.Top };
        page.Children.Add(top);
        var bottom = new Button { Height = 30, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(bottom, 2);
        page.Children.Add(bottom);
        page.EndInit();
        // The shell holds the view, and the view holds its grid.
        var fit = new PageFit { Child = new ContentControl { Content = new ContentControl { Content = page } } };
        return (fit, page, bottom, top);
    }

    private static void Lay(PageFit fit, double height)
    {
        fit.Measure(new Size(800, height));
        fit.Arrange(new Rect(0, 0, 800, height));
        fit.UpdateLayout();
    }

    [StaFact]
    public void APageThatFits_FillsTheWindow_AndDoesNotScroll()
    {
        var (fit, page, _, _) = Shell();

        Lay(fit, 1200);

        Assert.Equal(1200, fit.ExtentHeight);
        Assert.Equal(1200, fit.ViewportHeight);
        Assert.Equal(300, page.RowDefinitions[3].ActualHeight);
    }

    [StaFact]
    public void APageThatDoesNotFit_IsLaidOutAtItsMinimum_AndScrolls()
    {
        var (fit, page, _, _) = Shell();

        Lay(fit, 781);

        Assert.Equal(900 + PageGrid.StretchFloor, fit.ExtentHeight);
        Assert.Equal(781, fit.ViewportHeight);
        Assert.Equal(PageGrid.StretchFloor, page.RowDefinitions[3].ActualHeight);
    }

    [StaFact]
    public void APageWithoutAPageGrid_FillsTheWindow()
    {
        var fit = new PageFit { Child = new ContentControl { Content = new Grid { Height = 5000 } } };

        Lay(fit, 781);

        Assert.Equal(781, fit.ExtentHeight);
    }

    [StaFact]
    public void Scrolling_MovesThePage_AndStopsAtItsEnds()
    {
        var (fit, page, _, _) = Shell();
        Lay(fit, 781);
        var end = fit.ExtentHeight - fit.ViewportHeight;

        fit.LineDown();
        Assert.Equal(16, fit.VerticalOffset);
        fit.MouseWheelDown();
        Assert.Equal(16 + SystemParameters.WheelScrollLines * 16, fit.VerticalOffset);
        fit.PageDown();
        Assert.Equal(end, fit.VerticalOffset);
        fit.UpdateLayout();
        Assert.Equal(-end, page.TranslatePoint(default, fit).Y);

        fit.SetVerticalOffset(-50);
        Assert.Equal(0, fit.VerticalOffset);
        fit.SetVerticalOffset(10_000);
        Assert.Equal(end, fit.VerticalOffset);
    }

    [StaFact]
    public void ScrollingBack_StopsAtTheTop()
    {
        var (fit, _, _, _) = Shell();
        Lay(fit, 781);
        fit.SetVerticalOffset(200);

        fit.LineUp();
        Assert.Equal(184, fit.VerticalOffset);
        fit.MouseWheelUp();
        Assert.Equal(184 - SystemParameters.WheelScrollLines * 16, fit.VerticalOffset);
        fit.PageUp();
        Assert.Equal(0, fit.VerticalOffset);
    }

    [StaFact]
    public void ScrollingAcross_DoesNothing_AsThePageIsAsWideAsTheWindow()
    {
        var (fit, _, _, _) = Shell();
        Lay(fit, 781);
        fit.SetVerticalOffset(40);

        fit.LineLeft();
        fit.LineRight();
        fit.PageLeft();
        fit.PageRight();
        fit.MouseWheelLeft();
        fit.MouseWheelRight();
        fit.SetHorizontalOffset(100);

        Assert.Equal(0, fit.HorizontalOffset);
        Assert.Equal(40, fit.VerticalOffset);
        Assert.Equal(800, fit.ExtentWidth);
        Assert.Equal(800, fit.ViewportWidth);
    }

    [StaFact]
    public void InTheShellsScrollViewer_TheScrollBarCoversThePageAndMovesIt()
    {
        var (fit, page, _, _) = Shell();
        var scroller = new ScrollViewer
        {
            CanContentScroll = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = fit,
        };
        scroller.Measure(new Size(800, 781));
        scroller.Arrange(new Rect(0, 0, 800, 781));
        scroller.UpdateLayout();

        Assert.Same(scroller, fit.ScrollOwner);
        Assert.True(fit.CanVerticallyScroll);
        Assert.False(fit.CanHorizontallyScroll);
        Assert.Equal(fit.ExtentHeight, scroller.ExtentHeight);
        Assert.Equal(fit.ExtentHeight - fit.ViewportHeight, scroller.ScrollableHeight);

        scroller.ScrollToBottom();
        scroller.UpdateLayout();
        Assert.Equal(scroller.ScrollableHeight, fit.VerticalOffset);
        Assert.Equal(-fit.VerticalOffset, page.TranslatePoint(default, fit).Y);
    }

    [StaFact]
    public void OfferedAnEndlessHeight_ThePageIsShownAtItsMinimum()
    {
        var (fit, _, _, _) = Shell();

        fit.Measure(new Size(800, double.PositiveInfinity));

        Assert.Equal(900 + PageGrid.StretchFloor, fit.DesiredSize.Height);
    }

    [StaFact]
    public void WithNoPage_ThereIsNothingToScroll()
    {
        var fit = new PageFit();

        Lay(fit, 781);

        Assert.Equal(781, fit.ExtentHeight);
        Assert.Equal(0, fit.VerticalOffset);
        Assert.Equal(Rect.Empty, fit.MakeVisible(new Button(), new Rect(0, 0, 10, 10)));
    }

    [StaFact]
    public void AWindowMadeTaller_PullsTheOffsetBack()
    {
        var (fit, _, _, _) = Shell();
        Lay(fit, 781);
        fit.SetVerticalOffset(10_000);

        Lay(fit, 1200);

        Assert.Equal(0, fit.VerticalOffset);
        Assert.Equal(1200, fit.ExtentHeight);
    }

    [StaFact]
    public void AControlBelowTheWindow_IsScrolledUpTo_AndOneAboveItDownTo()
    {
        var (fit, _, bottom, top) = Shell();
        Lay(fit, 781);

        var shown = fit.MakeVisible(bottom, new Rect(bottom.RenderSize));
        fit.UpdateLayout();

        // The button's bottom, at 900, ends the window.
        Assert.Equal(900 - 781, fit.VerticalOffset);
        Assert.Equal(781 - 30, shown.Top);

        fit.MakeVisible(top, new Rect(top.RenderSize));
        Assert.Equal(0, fit.VerticalOffset);
    }
}
