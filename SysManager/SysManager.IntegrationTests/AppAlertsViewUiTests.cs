// SysManager · AppAlertsViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
/// New App Alerts really marks the alerts not yet acknowledged, and the mark goes when they are (#2884).
/// </summary>
/// <remarks>
/// The toolbar said "N new" and offered Acknowledge All, and no row showed which alerts were new. The unit tests prove
/// the count. They cannot prove a row shows the mark: a visibility bound the wrong way round compiles. So this loads
/// the real XAML on the STA thread with the app's dictionaries, as <see cref="RecentChangesViewUiTests"/> does. The
/// history is a fake written here, so nothing on this PC is read or written.
/// </remarks>
[Collection("Network")]
public sealed class AppAlertsViewUiTests
{
    private sealed class KeptHistory(params AppInstallEntry[] alerts) : IAppAlertHistory
    {
        public AppAlertsRead Load() => new(alerts, Readable: true);

        public void Add(AppInstallEntry alert) { }

        public void AcknowledgeAll() { }

        public void Rename(AppInstallEntry alert, string name, string publisher) { }

        public bool Clear() => true;
    }

    /// <summary>
    /// One alert not yet acknowledged and one that is, read from the history the way the tab starts, with that read
    /// finished. Built off the STA thread and handed to the view: it holds no thread-affine objects the view uses.
    /// </summary>
    private static async Task<AppAlertsViewModel> VmWithTwoAlertsAsync()
    {
        var vm = new AppAlertsViewModel(new AppAlertService(), new KeptHistory(
            new AppInstallEntry { Name = "Contoso Toolbar", DetectedAt = new DateTime(2026, 10, 20, 9, 0, 0) },
            new AppInstallEntry
            {
                Name = "Fabrikam Notes",
                DetectedAt = new DateTime(2026, 10, 19, 9, 0, 0),
                IsAcknowledged = true,
            }));
        await vm.InitializationComplete;
        return vm;
    }

    [Fact]
    public async Task AnAlertNotYetAcknowledged_IsMarkedNew_UntilAcknowledgeAll()
    {
        var vm = await VmWithTwoAlertsAsync();

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var view = new AppAlertsView { DataContext = vm };
            Relayout(view);

            Assert.Equal(["Contoso Toolbar New", "Fabrikam Notes"], NameCells(view));

            vm.AcknowledgeAllCommand.Execute(null);
            Relayout(view);

            Assert.Equal(["Contoso Toolbar", "Fabrikam Notes"], NameCells(view));
        });
    }

    /// <summary>The Name cell of each realised row, as the words shown in it.</summary>
    private static List<string> NameCells(FrameworkElement view)
    {
        var grid = Assert.Single(Descendants<DataGrid>(view),
            g => AutomationProperties.GetName(g) == "App installation alerts");
        var cells = Descendants<DataGridCell>(grid).Where(c => (string)c.Column.Header == "Name").ToList();
        Assert.True(cells.Count > 0, "no row was realised, so this would compare against nothing");
        return [.. cells.Select(c => string.Join(" ", ShownTexts(c)))];
    }

    private static void Relayout(FrameworkElement view)
    {
        view.Measure(new Size(1400, 1000));
        view.Arrange(new Rect(0, 0, 1400, 1000));
        view.UpdateLayout();
    }

    /// <summary>The text actually on screen under <paramref name="root"/>: a collapsed element is skipped.</summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
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
