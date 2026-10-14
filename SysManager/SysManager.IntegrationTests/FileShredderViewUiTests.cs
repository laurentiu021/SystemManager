// SysManager · FileShredderViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Helpers;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// File Shredder's queue is a grid like every other: the app's DataGrid style reaches it through the inline style
/// it sets for its empty state (#2694).
/// </summary>
/// <remarks>
/// The queue sets a <c>&lt;DataGrid.Style&gt;</c> to hide its column headers while it is empty, and that style had no
/// <c>BasedOn</c>. An explicit style replaces the implicit one instead of adding to it, so the queue lost all of the
/// app's grid style: its rows drew at 14 in Segoe UI where every other grid draws 13 in the app's font, with no
/// stripes, and without <c>RebuildFocus.Keep</c> a row's Remove button pressed from the keyboard took focus away with
/// the row, back to the window (#2609).
/// </remarks>
public class FileShredderViewUiTests
{
    [Fact]
    public void TheShredQueue_KeepsTheAppsGridStyle()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var view = new FileShredderView();
            view.Measure(new Size(1280, 900));
            view.Arrange(new Rect(0, 0, 1280, 900));
            var grid = Assert.Single(Descendants<DataGrid>(view));
            var resources = Application.Current!.Resources;

            Assert.True(RebuildFocus.GetKeep(grid),
                "the queue does not keep keyboard focus on its rows, so removing a file drops focus to the window");
            Assert.Equal(13d, grid.FontSize);
            Assert.Equal(((FontFamily)resources["UiFont"]).Source, grid.FontFamily.Source);
            Assert.Equal(((Brush)resources["Surface1"]).ToString(), grid.AlternatingRowBackground?.ToString());
            // Its own setter still applies over the app's.
            Assert.Equal(DataGridHeadersVisibility.Column, grid.HeadersVisibility);
        });
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
