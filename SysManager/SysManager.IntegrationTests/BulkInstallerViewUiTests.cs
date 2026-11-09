// SysManager · BulkInstallerViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// A long winget search leaves Bulk Installer's list of apps on the page, and the box's clear button puts the results
/// away (#2704).
/// </summary>
/// <remarks>
/// The search results sat in the search card, which grew with them, and the list of apps under it gets what the page
/// has left: thirty results took about 1,400 px, so at the default window the app list got nothing, and every result
/// past the page's edge, with its Add button, was out of reach. These lay the view out at the page the default window
/// gives a tab, with the app's resources and a winget that runs nothing.
/// </remarks>
public sealed class BulkInstallerViewUiTests
{
    /// <summary>The height of the page at the default window, 1280 by 820, as measured for #2704.</summary>
    private const int DefaultPageHeight = 781;

    [Fact]
    public async Task ThirtySearchResults_ScrollInsideTheCard_AndLeaveTheAppListOnThePage()
    {
        var icons = Directory.CreateTempSubdirectory("BulkInstallerView_Icons_");
        try
        {
            var vm = await WithThirtyResultsAsync(icons);

            StaHelper.Run(() =>
            {
                var view = Laid(vm);

                var page = Assert.IsType<PageGrid>(view.Content);
                var list = Assert.Single(page.Children.OfType<Border>(), b => Grid.GetRow(b) == 4);
                // 160 px is a category heading and two apps.
                Assert.True(list.ActualHeight >= 160,
                    $"the app list has {list.ActualHeight:F0} px of the page, under thirty search results");
                var items = Assert.Single(Descendants<ItemsControl>(view),
                    i => ReferenceEquals(i.ItemsSource, vm.SearchResults));
                var results = Assert.IsType<ScrollViewer>(Ancestors(items).FirstOrDefault(a => a is ScrollViewer));
                Assert.True(results.ScrollableHeight > 0, "the results under the first few cannot be scrolled to");
            });
        }
        finally
        {
            icons.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TheClearButton_ShowsWhileTheBoxHasText_AndPutsTheResultsAway()
    {
        var icons = Directory.CreateTempSubdirectory("BulkInstallerView_Icons_");
        try
        {
            var vm = await WithThirtyResultsAsync(icons);

            StaHelper.Run(() =>
            {
                var view = Laid(vm);
                var clear = Assert.Single(Descendants<Button>(view),
                    b => AutomationProperties.GetName(b) == "Clear the winget search");
                Assert.Same(vm.ClearSearchCommand, clear.Command);
                Assert.Equal(Visibility.Visible, clear.Visibility);

                clear.Command.Execute(clear.CommandParameter);
                view.UpdateLayout();

                Assert.Equal("", vm.SearchQuery);
                Assert.Empty(vm.SearchResults);
                Assert.Equal(Visibility.Collapsed, clear.Visibility);
            });
        }
        finally
        {
            icons.Delete(recursive: true);
        }
    }

    /// <summary>A view model over a winget that runs nothing, with "app" in the box and thirty results below.</summary>
    private static async Task<BulkInstallerViewModel> WithThirtyResultsAsync(DirectoryInfo icons)
    {
        var vm = new BulkInstallerViewModel(new BulkInstallerService(new RecordingRunner()),
            new AppIconService(null, icons.FullName));
        await vm.InitializationComplete;
        vm.SearchQuery = "app";
        vm.SearchResults.ReplaceWith(Enumerable.Range(1, 30).Select(i =>
            new InstallableApp { Name = $"Result {i}", WingetId = $"Vendor.App{i}", Category = "Search" }));
        return vm;
    }

    /// <summary>The view laid out on the page the default window gives a tab.</summary>
    private static BulkInstallerView Laid(BulkInstallerViewModel vm)
    {
        AppResources.Ensure();
        var view = new BulkInstallerView { DataContext = vm };
        var host = new Grid { Width = PageWidth.Default, Height = DefaultPageHeight };
        host.Children.Add(view);
        host.Measure(new Size(PageWidth.Default, DefaultPageHeight));
        host.Arrange(new Rect(0, 0, PageWidth.Default, DefaultPageHeight));
        host.UpdateLayout();
        return view;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
            yield return node;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
