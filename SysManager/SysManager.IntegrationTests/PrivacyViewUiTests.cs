// SysManager · PrivacyViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Privacy &amp; Telemetry really shows its switches by reach, and says which ones Apply will change (#1517).
/// </summary>
/// <remarks>
/// The unit tests prove the view model builds the sections and the counts. They cannot prove the view shows them: a
/// binding left on the wrong path compiles and leaves the list empty, or shows both lists at once. So this loads the
/// real XAML on the STA thread with the app's dictionaries, the way <see cref="SpeedTestViewUiTests"/> does. The
/// switches are read from a registry key of this test's own, which nothing writes: no switch is applied.
/// </remarks>
public sealed class PrivacyViewUiTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\PrivacyView_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public PrivacyViewUiTests() => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
    }

    /// <summary>Built off the STA thread over this test's own key, with a restore point that is never made.</summary>
    private async Task<PrivacyViewModel> VmAsync()
    {
        var vm = new PrivacyViewModel(new PrivacyService(hkcuRoot: _root, hklmRoot: _root),
            new SessionRestorePoint((_, _) => Task.FromResult(false), () => false), new PrivacyChoicesHandoff());
        await vm.InitializationComplete;
        return vm;
    }

    private static PrivacyView Laid(PrivacyViewModel vm)
    {
        AppResources.Ensure();
        var view = new PrivacyView { DataContext = vm };
        view.Measure(new Size(1200, 4000));
        view.Arrange(new Rect(0, 0, 1200, 4000));
        view.UpdateLayout();
        return view;
    }

    [Fact]
    public async Task ByReach_ShowsJustYouThenEveryone_InPlaceOfTheList()
    {
        var vm = await VmAsync();
        vm.Grouping = PrivacyGrouping.ByReach;

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var shown = ShownTexts(view);

            var justYou = shown.IndexOf("Just you — 8 switches");
            var everyone = shown.IndexOf("Everyone on this PC — 4 switches");
            Assert.True(justYou >= 0 && everyone > justYou, $"sections shown: {string.Join(" | ", shown)}");
            Assert.Contains("Change only your own account. No administrator needed.", shown);
            Assert.False(Shown(ItemsNamed(view, "Privacy switches")));
            Assert.Equal(12, Descendants<ToggleButton>(ItemsNamed(view, "Privacy switches by reach")).Count(t => t is not RadioButton));
        });
    }

    [Fact]
    public async Task ByTopic_ShowsTheOneList_AndNoSections()
    {
        var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            Assert.True(Shown(ItemsNamed(view, "Privacy switches")));
            Assert.False(Shown(ItemsNamed(view, "Privacy switches by reach")));
            Assert.DoesNotContain("Just you — 8 switches", ShownTexts(view));
        });
    }

    [Fact]
    public async Task ThePills_ShowAndSetTheGrouping()
    {
        var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var byTopic = Assert.Single(Descendants<RadioButton>(view), r => (string)r.Content == "By topic");
            var byReach = Assert.Single(Descendants<RadioButton>(view), r => (string)r.Content == "By reach");
            Assert.True(byTopic.IsChecked);
            Assert.False(byReach.IsChecked);

            byReach.IsChecked = true;

            Assert.Equal(PrivacyGrouping.ByReach, vm.Grouping);
            Assert.False(byTopic.IsChecked);
        });
    }

    [Fact]
    public async Task AMovedSwitch_SaysItChangesWhenYouApply_AndApplySaysHowMany()
    {
        var vm = await VmAsync();
        var moved = vm.Toggles[0];
        moved.IsEnabled = !moved.IsEnabled;

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var rows = Descendants<ContentPresenter>(ItemsNamed(view, "Privacy switches"))
                .Where(p => p.Content is PrivacyToggle).ToList();

            var movedRow = Assert.Single(rows, r => ReferenceEquals(r.Content, moved));
            Assert.Contains("changes when you apply", ShownTexts(movedRow));
            Assert.All(rows.Where(r => !ReferenceEquals(r.Content, moved)),
                r => Assert.DoesNotContain("changes when you apply", ShownTexts(r)));

            var apply = Assert.Single(Descendants<Button>(view), b => b.Command == vm.ApplyChangesCommand);
            Assert.Equal("Apply 1 change", apply.Content);
            Assert.Equal("Apply 1 change", AutomationProperties.GetName(apply));
        });
    }

    private static ItemsControl ItemsNamed(DependencyObject root, string name) =>
        Assert.Single(Descendants<ItemsControl>(root), c => AutomationProperties.GetName(c) == name);

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

    /// <summary>The text actually on screen under <paramref name="root"/>, in tree order.</summary>
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
