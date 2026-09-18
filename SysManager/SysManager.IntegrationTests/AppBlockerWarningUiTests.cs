// SysManager · AppBlockerWarningUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The rescue banner is really WIRED, not just present in the view model.
/// </summary>
/// <remarks>
/// The unit tests prove the view model computes the right words. They cannot prove the view shows them —
/// a mistyped binding path resolves to nothing, raises no build error and leaves a banner that is
/// permanently invisible, which is this codebase's most repeated defect: a surface that is tested and
/// bound by nothing. So this loads the real XAML on the STA thread with the app's dictionaries and reads
/// the rendered values back (#2357).
/// <para>It also covers the row shift. Adding the banner pushed every <c>Grid.Row</c> below it up by one,
/// and getting one wrong stacks two sections on top of each other — visible instantly when the app runs
/// and invisible to every other check.</para>
/// </remarks>
public class AppBlockerWarningUiTests
{
    /// <summary>
    /// A blocked list with nothing behind it. Hand-written rather than a mocking framework because this
    /// project does not reference one, and adding a package for four stub methods would be a heavier change
    /// than the class it replaces.
    /// </summary>
    private sealed class FakeBlocker(IReadOnlyList<BlockedApp> apps) : IAppBlockerService
    {
        public IReadOnlyList<BlockedApp> GetBlockedApps() => apps;

        // Never exercised here: this fixture is about what the view DISPLAYS. Throwing rather than
        // returning a plausible value means a future test that reaches for one is told to widen the fake
        // instead of quietly asserting against a made-up answer.
        public bool BlockApp(string exeName) => throw new NotSupportedException();
        public AppBlockerService.BlockResult TryBlockApp(string exeName) => throw new NotSupportedException();
        public bool UnblockApp(string exeName) => throw new NotSupportedException();
        public bool IsBlocked(string exeName) => throw new NotSupportedException();
    }

    private static AppBlockerViewModel VmWith(bool elevated, params (string Name, bool Unrecoverable)[] rows)
    {
        var blocker = new FakeBlocker([.. rows.Select(r => new BlockedApp
        {
            ExecutableName = r.Name,
            IsUnrecoverable = r.Unrecoverable,
        })]);

        var vm = new AppBlockerViewModel(blocker);
        vm.InitializationComplete.GetAwaiter().GetResult();
        vm.IsElevated = elevated;
        vm.RefreshListCommand.Execute(null);
        return vm;
    }

    /// <summary>The warning Border is the one whose Visibility is bound; find it by that binding.</summary>
    private static Border? WarningBanner(DependencyObject root)
    {
        if (root is Border b
            && System.Windows.Data.BindingOperations.GetBinding(b, UIElement.VisibilityProperty) is { } binding
            && binding.Path?.Path == nameof(AppBlockerViewModel.HasUnrecoverableBlock))
            return b;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (WarningBanner(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }

    [Fact]
    public void TheView_Parses_WithTheBannerAndTheShiftedRows()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var view = new AppBlockerView();
            Assert.NotNull(view);

            // Seven rows now, not six: header, banner, elevation, input, toolbar, list, footer. Asserted
            // because a missing RowDefinition puts the footer in the same cell as the grid and nothing
            // else notices.
            var grid = (Grid)view.Content;
            Assert.Equal(7, grid.RowDefinitions.Count);

            // No child may sit outside the rows that exist.
            foreach (UIElement child in grid.Children)
                Assert.InRange(Grid.GetRow(child), 0, grid.RowDefinitions.Count - 1);
        });
    }

    [Fact]
    public void TheBanner_IsHidden_WhenNothingIsStranded()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var view = new AppBlockerView { DataContext = VmWith(elevated: true, ("notepad.exe", false)) };
            view.Measure(new Size(1200, 900));
            view.Arrange(new Rect(0, 0, 1200, 900));
            view.UpdateLayout();

            var banner = WarningBanner(view);
            Assert.NotNull(banner);
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
        });
    }

    [Fact]
    public void TheBanner_ShowsTheViewModelsWords_WhenStranded()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var vm = VmWith(elevated: false, ("consent.exe", true));
            var view = new AppBlockerView { DataContext = vm };
            view.Measure(new Size(1200, 900));
            view.Arrange(new Rect(0, 0, 1200, 900));
            view.UpdateLayout();

            var banner = WarningBanner(view);
            Assert.NotNull(banner);
            Assert.Equal(Visibility.Visible, banner.Visibility);

            // The rendered text, read off the control rather than off the view model: this is the step
            // that would catch a bound-to-nothing TextBlock beside a correctly computed property.
            var shown = new List<string>();
            CollectText(banner, shown);
            Assert.Contains(vm.UnrecoverableWarning, shown);
            Assert.NotEqual("", vm.UnrecoverableWarning);
        });
    }

    private static void CollectText(DependencyObject root, List<string> into)
    {
        if (root is TextBlock tb) into.Add(tb.Text);
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            CollectText(System.Windows.Media.VisualTreeHelper.GetChild(root, i), into);
    }
}
