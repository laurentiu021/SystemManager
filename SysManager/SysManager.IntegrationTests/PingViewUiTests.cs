// SysManager · PingViewUiTests
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
/// The Ping chart's window picker really reads in minutes and still sets the window in seconds (#2884).
/// </summary>
/// <remarks>
/// The unit test proves the options' labels. It cannot prove the picker shows them: the app's ComboBox template does
/// not pass an item format to the closed box, and a picker whose value path is misspelt compiles and sets nothing. So
/// this loads the real XAML on the STA thread with the app's dictionaries, as <see cref="SpeedTestViewUiTests"/> does.
/// Nothing is pinged: the monitor is never started.
/// </remarks>
[Collection("Network")]
public sealed class PingViewUiTests
{
    [Fact]
    public void TheWindowPicker_ReadsInMinutes_AndSetsTheWindowInSeconds()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            using var shared = new NetworkSharedState(new PingMonitorService(), new TracerouteService(),
                new TracerouteMonitorService(), new SpeedTestService(),
                new NetworkRepairService(new PowerShellRunner()));
            var view = new PingView { DataContext = new PingViewModel(shared) };
            Relayout(view);

            var picker = Assert.Single(Descendants<ComboBox>(view),
                c => AutomationProperties.GetName(c) == "Chart time window in minutes");
            Assert.Equal(60, Assert.IsType<ChartWindowOption>(picker.SelectedItem).Seconds);
            Assert.Contains("1 min", TextsOf(picker));

            picker.SelectedIndex = 1;
            Relayout(view);

            Assert.Equal(300, shared.WindowSeconds);
            Assert.Contains("5 min", TextsOf(picker));
            Assert.DoesNotContain("300", TextsOf(picker));
        });
    }

    private static void Relayout(FrameworkElement view)
    {
        view.Measure(new Size(1400, 1000));
        view.Arrange(new Rect(0, 0, 1400, 1000));
        view.UpdateLayout();
    }

    private static List<string> TextsOf(DependencyObject root) =>
        [.. Descendants<TextBlock>(root).Select(t => t.Text).Where(t => t.Length > 0)];

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
