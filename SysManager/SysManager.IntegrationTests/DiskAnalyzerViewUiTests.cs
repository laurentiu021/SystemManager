// SysManager · DiskAnalyzerViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Disk Analyzer's map really draws its blocks, sized to the space the view gives it (#1592).
/// </summary>
/// <remarks>
/// The unit tests prove which blocks the map holds for a given size. They cannot prove the view reports its size, that
/// a block is a button bound to the drill-in command, or that its colours reach the screen: a binding left on the wrong
/// path compiles and draws nothing. So this loads the real XAML on the STA thread with the app's dictionaries, the way
/// <see cref="SpeedTestViewUiTests"/> does. The entries are constructed; nothing scans a disk.
/// </remarks>
public sealed class DiskAnalyzerViewUiTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was written */ }
    }

    private static DiskUsageEntry Folder(string name, double gb) => new()
    {
        Name = name,
        FullPath = @"C:\Users\Ana\" + name,
        SizeBytes = (long)(gb * Gb),
    };

    private static List<DiskUsageEntry> Profile() =>
    [
        Folder("AppData", 38), Folder("Videos", 22), Folder("Downloads", 14), Folder("Documents", 6),
        Folder("Pictures", 4.5), Folder("Contacts", 0.01),
        new() { Name = DiskAnalyzerService.LooseFilesName, FullPath = @"C:\Users\Ana", SizeBytes = Gb / 4 },
    ];

    /// <summary>Built off the STA thread with its own folders for history and preferences, then shown the entries.</summary>
    private async Task<DiskAnalyzerViewModel> VmShowingAsync(List<DiskUsageEntry> entries)
    {
        var vm = new DiskAnalyzerViewModel(new DiskAnalyzerService(),
            new DiskScanHistoryService(Path.Combine(_dir, "history")),
            new DiskAnalyzerPreferenceService(Path.Combine(_dir, "preferences")));
        await vm.InitializationComplete;
        vm.Entries.ReplaceWith(entries);
        vm.Map.Update(entries);
        return vm;
    }

    private static DiskAnalyzerView Laid(DiskAnalyzerViewModel vm)
    {
        AppResources.Ensure();
        var view = new DiskAnalyzerView { DataContext = vm };
        // Twice: the first pass reports the map's size, and the blocks laid out for it need a pass of their own.
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1200, 1000));
            view.Arrange(new Rect(0, 0, 1200, 1000));
            view.UpdateLayout();
        }
        return view;
    }

    private static ItemsControl MapOf(DependencyObject view) =>
        Assert.Single(Descendants<ItemsControl>(view), c => c is not ListView && c.ItemsSource is IEnumerable<TreemapTile>);

    private static List<Button> BlocksOf(ItemsControl map) => [.. Descendants<Button>(map)];

    [Fact]
    public async Task TheMap_IsLaidOutForTheWidthTheViewGivesIt()
    {
        var vm = await VmShowingAsync(Profile());

        StaHelper.Run(() =>
        {
            var map = MapOf(Laid(vm));

            Assert.Equal(Visibility.Visible, map.Visibility);
            Assert.True(map.ActualWidth > 900, $"the map is {map.ActualWidth:F0} wide");
            Assert.Equal(map.ActualWidth, vm.Map.Width, 3);
            Assert.Equal(240, vm.Map.Height, 3);
            Assert.Equal(vm.Map.Tiles.Count, BlocksOf(map).Count);
        });
    }

    [Fact]
    public async Task EachFolderBlock_IsAButtonThatOpensItsFolder_AndSaysWhatItIs()
    {
        var vm = await VmShowingAsync(Profile());

        StaHelper.Run(() =>
        {
            var blocks = BlocksOf(MapOf(Laid(vm)));
            var appData = Assert.Single(blocks, b => ((TreemapTile)b.DataContext).Name == "AppData");
            var tile = (TreemapTile)appData.DataContext;

            Assert.Same(vm.DrillDownCommand, appData.Command);
            Assert.Same(tile.Entry, appData.CommandParameter);
            Assert.Equal(tile.SpokenName, AutomationProperties.GetName(appData));
            Assert.Equal(tile.ToolTipText, appData.ToolTip);
            Assert.True(appData.Focusable);
            Assert.NotNull(appData.ContextMenu);
            Assert.Contains("AppData", Descendants<TextBlock>(appData).Select(t => t.Text));

            // Each block in its own shade, with the text colour chosen for that shade: the largest is the accent itself,
            // so checking it alone would not tell its own fill from the accent's.
            var folders = blocks.Where(b => !((TreemapTile)b.DataContext).IsOther).ToList();
            Assert.True(folders.Count >= 3, $"only {folders.Count} folder blocks");
            foreach (var block in folders)
            {
                var shown = (TreemapTile)block.DataContext;
                Assert.Equal(shown.FillHex, Hex(block.Background));
                Assert.All(Descendants<TextBlock>(block).Where(Shown), t => Assert.Equal(shown.TextHex, Hex(t.Foreground)));
            }
        });
    }

    [Fact]
    public async Task TheOtherBlock_IsNotATabStop_AndHasNoMenu()
    {
        var vm = await VmShowingAsync(Profile());

        StaHelper.Run(() =>
        {
            var blocks = BlocksOf(MapOf(Laid(vm)));
            var other = Assert.Single(blocks, b => ((TreemapTile)b.DataContext).IsOther);

            Assert.False(other.Focusable);
            Assert.Null(other.ContextMenu);
            Assert.Null(other.CommandParameter);
        });
    }

    [Fact]
    public async Task HidingTheMap_LeavesOnlyTheButtonThatBringsItBack()
    {
        var vm = await VmShowingAsync(Profile());
        vm.ToggleMapCommand.Execute(null);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            Assert.Equal(Visibility.Collapsed, MapOf(view).Visibility);
            var toggle = Assert.Single(Descendants<Button>(view), b => b.Command == vm.ToggleMapCommand);
            Assert.Equal("Show map", toggle.Content);
            Assert.True(Shown(toggle));
            Assert.DoesNotContain(Descendants<TextBlock>(view),
                t => t.Text == "Click a block to open that folder" && Shown(t));
        });
    }

    [Fact]
    public async Task WithNoSubfolderToDraw_ThereIsNoMapAtAll()
    {
        var vm = await VmShowingAsync([new() { Name = DiskAnalyzerService.LooseFilesName, FullPath = @"C:\Users\Ana", SizeBytes = Gb }]);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var toggle = Assert.Single(Descendants<Button>(view), b => b.Command == vm.ToggleMapCommand);

            Assert.False(Shown(toggle));
            Assert.False(Shown(MapOf(view)));
        });
    }

    /// <summary>A solid brush's colour as "#RRGGBB", the form the tiles carry.</summary>
    private static string Hex(Brush brush) => ((SolidColorBrush)brush).Color.ToString().Remove(1, 2);

    /// <summary>
    /// Whether nothing between <paramref name="element"/> and the view is collapsed. <c>IsVisible</c> cannot say: it
    /// is false for anything not shown in a window, and these views are laid out without one.
    /// </summary>
    private static bool Shown(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
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
