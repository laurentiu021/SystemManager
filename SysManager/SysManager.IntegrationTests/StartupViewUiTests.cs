// SysManager · StartupViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Delete key on Startup Manager's list leaves the list alone (#2809).
/// </summary>
/// <remarks>
/// WPF's <c>DataGrid</c> takes the selected row off its collection on Delete unless it is told not to, and the row
/// vanished while the program still started with Windows. The guard reads the attribute on every editable grid; this
/// presses the key on the real XAML, so the attribute is seen to do what the guard trusts it to.
/// <para>The view is given only the list it shows, not a <c>StartupViewModel</c>: the view model's scan hands its
/// result to the application's dispatcher once a test has made one, and nothing pumps that dispatcher on
/// <see cref="StaHelper"/>'s thread, so waiting for the scan would wait forever. The grid is what is under test, and
/// it binds the list by name.</para>
/// </remarks>
[Collection("Network")]
public sealed class StartupViewUiTests
{
    /// <summary>What the view binds the grid to, and nothing else.</summary>
    private sealed class OnlyTheList
    {
        public ObservableCollection<StartupEntry> Entries { get; } =
        [
            new() { Name = "Contoso Updater", Command = @"C:\Program Files\Contoso\update.exe", Publisher = "Contoso",
                    Source = StartupSource.RegistryCurrentUser },
            new() { Name = "Fabrikam Sync", Command = @"C:\Program Files\Fabrikam\sync.exe", Publisher = "Fabrikam",
                    Source = StartupSource.RegistryCurrentUser },
        ];
    }

    [Fact]
    public void TheDeleteKey_LeavesTheListAlone()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var shown = new OnlyTheList();
            var view = new StartupView { DataContext = shown };
            view.Measure(new Size(1400, 1000));
            view.Arrange(new Rect(0, 0, 1400, 1000));
            view.UpdateLayout();

            var grid = Assert.Single(Descendants<DataGrid>(view),
                g => AutomationProperties.GetName(g) == "Startup programs");
            Assert.Equal(2, grid.Items.Count);
            grid.SelectedIndex = 0;

            Assert.False(DataGrid.DeleteCommand.CanExecute(null, grid));
            DataGrid.DeleteCommand.Execute(null, grid);

            Assert.Equal(["Contoso Updater", "Fabrikam Sync"], shown.Entries.Select(e => e.Name));
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
