// SysManager · DarkModeViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// A time box on Dark Mode Scheduler shows the time the schedule runs on once it is left: a time the schedule reads is
/// written as it reads it, and one it cannot read is put back (#2760).
/// </summary>
/// <remarks>
/// The view model writes the box's property again from inside the setter the binding called, and its unit tests prove
/// that. Only the real binding proves the box then shows that time rather than what was typed, which is how the user sees
/// the entry was refused. The theme service is a fake written here, so no theme is switched and no schedule is saved.
/// </remarks>
public class DarkModeViewUiTests
{
    private sealed class FakeThemes : IWindowsThemeService
    {
        public WindowsTheme GetCurrentTheme() => WindowsTheme.Light;

        public bool SetTheme(bool dark, bool includeSystem) => true;

        public DarkModeSchedule LoadSchedule() => new() { Enabled = true, DarkStart = "21:00", LightStart = "07:00" };

        public bool SaveSchedule(DarkModeSchedule schedule) => true;
    }

    private static T Laid<T>(T view) where T : FrameworkElement
    {
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(0, 0, 1200, 900));
        view.UpdateLayout();
        return view;
    }

    /// <summary>Types into the box and leaves it, which is when the box hands its text over.</summary>
    private static void Type(TextBox box, string text)
    {
        box.Text = text;
        var typed = BindingOperations.GetBindingExpression(box, TextBox.TextProperty);
        Assert.True(typed is not null, "the box's text is bound to nothing, so what is typed into it goes nowhere");
        typed.UpdateSource();
    }

    [Theory]
    [InlineData("19h", "21:00")]
    [InlineData("7:30", "07:30")]
    public void ATimeBox_ShowsTheTimeTheScheduleRunsOn_OnceItIsLeft(string typed, string shown)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            using var vm = new DarkModeViewModel(new FakeThemes());
            var view = Laid(new DarkModeView { DataContext = vm });
            var box = Assert.Single(Descendants<TextBox>(view),
                b => AutomationProperties.GetName(b) == "Dark starts at — a time as HH:mm");

            Type(box, typed);

            Assert.Equal(shown, vm.DarkStart);
            Assert.Equal(shown, box.Text);
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
