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
/// its parent cuts across. A row a page leaves too little height is #2704's, and is not read here. Text a view fills in
/// from its data is not there to be measured, so this holds the views' own rows,
/// toolbars and notes; <c>ArchitectureTests.NoTextWrappingOrTrimming_IsInertInsideAHorizontalStackPanel</c> holds the
/// rest by reading the markup.
/// <para>Inside a <see cref="DataGrid"/> nothing is read, though the grid itself is: it works out its column widths in
/// a dispatcher pass after layout, which this harness does not run, so its headers measure as cut here and are not on
/// screen.</para>
/// </remarks>
public sealed class ViewLayoutTests
{
    /// <summary>
    /// The rows #2704 is fixing, still cut. Each lays a description out before the buttons beside it, so the buttons
    /// get what is left, or, on Speed Test, gives the Ookla server picker the room the card's title and Run leave. Each
    /// must still be cut, so this empties as #2704 lands instead of outliving it.
    /// </summary>
    private static readonly (int Width, string View, string Element)[] CutUntil2704 =
    [
        (PageWidth.Default, "NetworkRepairView", "the Button \"Flush DNS resolver cache\""),
        (PageWidth.Default, "NetworkRepairView", "a Border holding \"Flush DNS\""),
        (PageWidth.Default, "NetworkRepairView", "the text \"Flush DNS\""),
        (PageWidth.Default, "NetworkRepairView", "the text \"Reset Winsock\""),
        (PageWidth.Default, "NetworkRepairView", "the text \"Reset TCP/IP\""),
        (PageWidth.Default, "PingView", "a StackPanel holding \"Start\""),
        (PageWidth.Default, "ResourceHistoryView", "a StackPanel holding \"Range\""),
        (PageWidth.Default, "SettingsWatchdogView", "a StackPanel holding \"Save baseline\""),
        (PageWidth.Default, "SystemHealthView", "the text \"Scan\""),
        (PageWidth.Default, "TracerouteView", "a StackPanel holding \"Start auto-trace\""),
        (PageWidth.Smallest, "NetworkRepairView", "the Button \"Flush DNS resolver cache\""),
        (PageWidth.Smallest, "NetworkRepairView", "a Border holding \"Flush DNS\""),
        (PageWidth.Smallest, "NetworkRepairView", "the text \"Flush DNS\""),
        (PageWidth.Smallest, "NetworkRepairView", "the Button \"Reset Winsock catalog\""),
        (PageWidth.Smallest, "NetworkRepairView", "a Border holding \"Reset Winsock\""),
        (PageWidth.Smallest, "NetworkRepairView", "the text \"Reset Winsock\""),
        (PageWidth.Smallest, "NetworkRepairView", "the Button \"Reset TCP/IP stack\""),
        (PageWidth.Smallest, "NetworkRepairView", "a Border holding \"Reset TCP/IP\""),
        (PageWidth.Smallest, "NetworkRepairView", "the text \"Reset TCP/IP\""),
        (PageWidth.Smallest, "PingView", "a StackPanel holding \"Start\""),
        (PageWidth.Smallest, "ResourceHistoryView", "a StackPanel holding \"Range\""),
        (PageWidth.Smallest, "SettingsWatchdogView", "a StackPanel holding \"Save baseline\""),
        (PageWidth.Smallest, "SpeedTestView", "the ComboBox \"Ookla server\""),
        (PageWidth.Smallest, "SystemHealthView", "the Button \"Scan system health\""),
        (PageWidth.Smallest, "SystemHealthView", "the text \"Scan\""),
        (PageWidth.Smallest, "TracerouteView", "a StackPanel holding \"Start auto-trace\""),
    ];

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

            var known = CutUntil2704.Where(k => k.Width == pageWidth).ToList();
            var unexpected = cut.Where(c => !known.Any(k => k.View == c.View && k.Element == c.Element))
                .Select(c => $"{c.View}: {c.Element} is {c.Size}")
                .ToList();
            Assert.True(unexpected.Count == 0,
                $"at a page {pageWidth} px wide these are cut instead of wrapping. A row that can be wider than the "
                + "page is a WrapPanel, as the Uninstaller's toolbar is, and a note is a DockPanel with its glyph "
                + "docked left:\n  " + string.Join("\n  ", unexpected));

            var fixedNow = known.Where(k => !cut.Any(c => c.View == k.View && c.Element == k.Element))
                .Select(k => $"{k.View}: {k.Element}")
                .ToList();
            Assert.True(fixedNow.Count == 0,
                $"at a page {pageWidth} px wide these are no longer cut, so take them off CutUntil2704:\n  "
                + string.Join("\n  ", fixedNow));
        });
    }
}
