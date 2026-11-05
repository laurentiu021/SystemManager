// SysManager · ConsoleViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The live output five tabs stream an operation into, laid out without a window: what a screen reader is told each
/// line is.
/// </summary>
public sealed class ConsoleViewUiTests
{
    [Fact]
    public void EachLine_IsAnnouncedByItsText()
    {
        // Each line's record read out every field, "PowerShellLine { Kind = Output, Text = …, Timestamp = … }",
        // on every line (#2785).
        var vm = new ConsoleViewModel();

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            vm.Lines.Add(PowerShellLine.Output(@"Scanning C:\Windows\Temp"));
            vm.Lines.Add(PowerShellLine.Err("Access is denied."));
            var view = new ConsoleView { DataContext = vm };
            view.Measure(new Size(900, 600));
            view.Arrange(new Rect(0, 0, 900, 600));
            view.UpdateLayout();

            var list = Assert.Single(Descendants<ListBox>(view));
            var items = UIElementAutomationPeer.CreatePeerForElement(list).GetChildren() ?? [];
            var names = items.Select(p => p.GetName());

            Assert.Equal([@"Scanning C:\Windows\Temp", "Access is denied."], names);
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
