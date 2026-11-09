// SysManager · ViewLayoutTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;

namespace SysManager.IntegrationTests;

/// <summary>
/// Every view, laid out at the default window and at the smallest, cuts nothing it shows (#2787).
/// </summary>
/// <remarks>
/// A horizontal StackPanel gives what is inside it all the width it asks for, so a row too wide for the page is cut at
/// the card's edge instead of wrapping. That cut the caveat off Speed Test's note and the end of every line of Deep
/// Cleanup's, and Refresh, Cancel and the search box off System Logs' filter bar near the smallest window. This builds
/// every view with the app's resources and no data, lays it out at both page widths, and fails on any laid-out element
/// its parent cuts across. A row a page leaves too little height is <c>PageHeightTests</c>', and is not read here.
/// Text a view fills in from its data is not there to be measured, so this holds the views' own rows, toolbars and
/// notes; <c>ArchitectureTests.NoTextWrappingOrTrimming_IsInertInsideAHorizontalStackPanel</c> holds the rest by
/// reading the markup.
/// <para>Inside a <see cref="DataGrid"/> nothing is read, though the grid itself is: it works out its column widths in
/// a dispatcher pass after layout, which this harness does not run, so its headers measure as cut here and are not on
/// screen.</para>
/// </remarks>
public sealed class ViewLayoutTests
{
    [Theory]
    [InlineData(PageWidth.Default)]
    [InlineData(PageWidth.Smallest)]
    public void EveryView_CutsNothing(int pageWidth)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var views = AppViews.Constructible();
            Assert.True(views.Count >= 60, $"only {views.Count} views were found, where there are 67.");

            var cut = new List<(string View, string Element, string Size)>();
            var read = 0;
            foreach (var type in views)
            {
                var view = (UserControl)Activator.CreateInstance(type)!;
                var host = new Grid { Width = pageWidth, Height = 900 };
                host.Children.Add(view);
                host.Measure(new Size(pageWidth, 900));
                host.Arrange(new Rect(0, 0, pageWidth, 900));
                host.UpdateLayout();

                foreach (var element in LayoutClip.LaidOut(view, lookInside: e => e is not DataGrid))
                {
                    read++;
                    if (!LayoutClip.IsCutAcross(element, out var room)) continue;
                    var size = $"{element.ActualWidth:F0} x {element.ActualHeight:F0}";
                    cut.Add((type.Name, LayoutClip.Describe(element), $"{size}, shown {LayoutClip.Shown(room)}"));
                }
            }

            // Vacuity floor: 6,333 elements laid out at each width when measured (#2787).
            Assert.True(read >= 5500,
                $"only {read} elements were laid out across {views.Count} views, out of 6,333 measured, so a "
                + "pass means little.");

            Assert.True(cut.Count == 0,
                $"at a page {pageWidth} px wide these are cut instead of wrapping. A row that can be wider than the "
                + "page is a WrapPanel, as the Uninstaller's toolbar is, and a note is a DockPanel with its glyph "
                + "docked left:\n  " + string.Join("\n  ", cut.Select(c => $"{c.View}: {c.Element} is {c.Size}")));
        });
    }
}
