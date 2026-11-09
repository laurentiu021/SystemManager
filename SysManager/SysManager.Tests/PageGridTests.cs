// SysManager · PageGridTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// <see cref="PageGrid"/> keeps a page's star rows a floor, and says how tall the page has to be for the shell's
/// <see cref="PageFit"/> (#2704).
/// </summary>
public class PageGridTests
{
    /// <summary>Three fixed rows of 100 over a star row holding a list 2,000 tall, as a page's list is.</summary>
    private static PageGrid Page(Thickness margin = default, double? floor = null)
    {
        var page = new PageGrid { Margin = margin };
        page.BeginInit();
        for (var r = 0; r < 3; r++)
        {
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var fixedRow = new Border { Height = 100 };
            Grid.SetRow(fixedRow, r);
            page.Children.Add(fixedRow);
        }
        var stretch = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        if (floor is { } own) stretch.MinHeight = own;
        page.RowDefinitions.Add(stretch);
        var list = new ScrollViewer { Content = new Border { Height = 2000 } };
        Grid.SetRow(list, 3);
        page.Children.Add(list);
        page.EndInit();
        return page;
    }

    [StaFact]
    public void AStarRow_WithNoFloorOfItsOwn_GetsTheStretchFloor_AndAFixedRowNone()
    {
        var page = Page();

        Assert.Equal(PageGrid.StretchFloor, page.RowDefinitions[3].MinHeight);
        Assert.All(page.RowDefinitions.Take(3), row => Assert.Equal(0, row.MinHeight));
    }

    [StaFact]
    public void AStarRow_WithAFloorOfItsOwn_KeepsIt()
    {
        var page = Page(floor: 240);

        Assert.Equal(240, page.RowDefinitions[3].MinHeight);
    }

    [StaFact]
    public void OfferedLessThanItNeeds_ThePageRequiresItsFixedRowsAndFloors_WithItsMargin()
    {
        // FrameworkElement cuts the desired size to the 300 offered; the grid's own measure still asks for 460.
        var page = Page(new Thickness(0, 24, 0, 16));

        page.Measure(new Size(800, 300));

        Assert.Equal(300, page.DesiredSize.Height);
        Assert.Equal(300 + PageGrid.StretchFloor + 40, page.Required);
    }

    [StaFact]
    public void OfferedRoom_ThePageRequiresWhatItIsOffered()
    {
        var page = Page(new Thickness(0, 24, 0, 16));

        page.Measure(new Size(800, 1000));

        Assert.Equal(1000, page.Required);
    }
}
