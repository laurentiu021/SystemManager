// SysManager · RecentChangesViewUiTests
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
/// Recent Changes really draws what it found (#1507): the days and their rows, the button on a row, a day's updates
/// opening to their list, the line about problems, the empty state, and chips that set what the tab filters on.
/// </summary>
/// <remarks>
/// The unit tests prove the view model builds the days and the words. They cannot prove the view shows them: a binding
/// on the wrong path compiles and draws nothing, and a chip whose parameter is misspelt sets a value nothing matches.
/// So this loads the real XAML on the STA thread with the app's dictionaries, as <see cref="UndoChangesViewUiTests"/>
/// does. The service is a fake written here, so nothing on this PC is read.
/// </remarks>
[Collection("Network")]
public sealed class RecentChangesViewUiTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 15, 0, 0);

    private sealed class FakeRecentChanges(RecentChangesLook look) : IRecentChangesService
    {
        public Task<RecentChangesLook> LookAsync(int days, CancellationToken ct = default) => Task.FromResult(look);
    }

    private sealed class NoNavigation : INavigationService
    {
        public void GoTo(string navId, string? filter = null) { }
    }

    private static readonly ChangeEvent[] AWeek =
    [
        new(Now.AddHours(-4), null, ChangeKind.StoreAppUpdate, "Contoso.PhotoEditor", "Microsoft Store", ""),
        new(Now.AddHours(-5), null, ChangeKind.WindowsUpdate, "KB5065426", "Windows Update", ""),
        new(Now.AddHours(-6), null, ChangeKind.SysManagerAction, "Turned on", "You, in SysManager", "Performance Mode"),
        new(Now.AddDays(-1), null, ChangeKind.SettingChanged, "Advertising ID", "Windows or another program",
            "Found On, where you saved Off. Settings Watchdog noticed at 15:00."),
        new(Now.AddDays(-3), Now.AddDays(-6), ChangeKind.ProgramAppeared, "Search Pro Toolbar", "An installer",
            "Installed some time between your look on Fri 2 Oct and 15:00"),
    ];

    private static async Task<RecentChangesViewModel> VmAsync(IReadOnlyList<ChangeEvent> changes, IReadOnlyList<ProblemEvent>? problems = null)
    {
        var vm = new RecentChangesViewModel(
            new FakeRecentChanges(new RecentChangesLook(changes, problems ?? [], [], FirstProgramsLook: false, HasBaseline: true, Now)),
            new NoNavigation());
        await vm.InitializationComplete;
        return vm;
    }

    private static RecentChangesView Laid(RecentChangesViewModel vm)
    {
        AppResources.Ensure();
        var view = new RecentChangesView { DataContext = vm };
        Relayout(view);
        return view;
    }

    private static void Relayout(FrameworkElement view)
    {
        view.Measure(new Size(1200, 2400));
        view.Arrange(new Rect(0, 0, 1200, 2400));
        view.UpdateLayout();
    }

    [Fact]
    public async Task EachDay_ShowsItsHeading_AndEachRowItsWords()
    {
        var vm = await VmAsync(AWeek);

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Named<ItemsControl>(Laid(vm), "Recent changes, by day"));

            Assert.Equal(["Today", "Yesterday", "Mon 5 Oct"], shown.Where(t => t is "Today" or "Yesterday" or "Mon 5 Oct"));
            Assert.Contains("Windows installed 2 updates", shown);
            Assert.Contains("1 app update from the Microsoft Store, 1 Windows update", shown);
            Assert.Contains("Turned on", shown);
            Assert.Contains("You, in SysManager", shown);
            Assert.Contains("Performance Mode", shown);
            Assert.Contains("Advertising ID changed", shown);
            Assert.Contains("—", shown);
            Assert.Contains("New program: Search Pro Toolbar", shown);
            Assert.Contains("Installed some time between your look on Fri 2 Oct and 15:00", shown);
        });
    }

    [Fact]
    public async Task ARowsButton_OpensItsTab_ThroughTheTabsCommand()
    {
        var vm = await VmAsync(AWeek);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            var uninstaller = Named<Button>(view, "Open Uninstaller: New program: Search Pro Toolbar");
            Assert.Equal("Open Uninstaller", uninstaller.Content);
            Assert.Same(vm.OpenTabCommand, uninstaller.Command);
            Assert.Equal("nav-uninstaller", uninstaller.CommandParameter);

            var review = Named<Button>(view, "Review: Advertising ID changed");
            Assert.Same(vm.OpenTabCommand, review.Command);
            Assert.Equal("nav-settings-watchdog", review.CommandParameter);

            // A row with nowhere to send you has no button at all, rather than one that does nothing.
            Assert.DoesNotContain(Descendants<Button>(view), b => Shown(b) && AutomationProperties.GetName(b).EndsWith(": Turned on", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ADaysUpdates_OpenToTheirList_AndCloseAgain()
    {
        var vm = await VmAsync(AWeek);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var item = "• An app was updated from the Microsoft Store: Contoso.PhotoEditor";
            var toggle = Named<Button>(view, "Show all 2 updates");
            Assert.Equal("Show all 2", toggle.Content);
            Assert.DoesNotContain(item, ShownTexts(view));

            toggle.Command.Execute(toggle.CommandParameter);
            Relayout(view);

            Assert.Contains(item, ShownTexts(view));
            Assert.Contains("• Windows installed an update: KB5065426", ShownTexts(view));
            Assert.Equal("Hide the list of updates", AutomationProperties.GetName(toggle));
        });
    }

    [Fact]
    public async Task TheProblemsLine_SaysHowMany_AndOpensSystemLogs()
    {
        var vm = await VmAsync(AWeek, [new ProblemEvent(Now.AddDays(-2), StoppedResponding: false)]);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            Assert.Contains("Problems in the same days — 1 app crash — are in System Logs.", ShownTexts(view));
            var logs = Named<Button>(view, "Open System Logs");
            Assert.Same(vm.OpenTabCommand, logs.Command);
            Assert.Equal("nav-logs", logs.CommandParameter);
        });
    }

    [Fact]
    public async Task WithNoProblems_ThereIsNoProblemsLine()
    {
        var vm = await VmAsync(AWeek);

        StaHelper.Run(() => Assert.False(Shown(Named<Button>(Laid(vm), "Open System Logs"))));
    }

    [Fact]
    public async Task NothingInThePeriod_ShowsTheEmptyState_AndNoList()
    {
        var vm = await VmAsync([]);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var shown = ShownTexts(view);

            Assert.Contains("No changes in the last 7 days", shown);
            Assert.Contains("Nothing was installed or updated, SysManager changed nothing, and your saved settings still match. "
                + "Pick a longer period above to look further back.", shown);
            Assert.False(Shown(Named<ItemsControl>(view, "Recent changes, by day")));
        });
    }

    [Fact]
    public async Task WithChanges_TheEmptyStateIsNotShown()
    {
        var vm = await VmAsync(AWeek);

        StaHelper.Run(() => Assert.DoesNotContain("No changes in the last 7 days", ShownTexts(Laid(vm))));
    }

    [Fact]
    public async Task EveryKindChip_SetsTheKindItSays_AndTheListFollows()
    {
        var vm = await VmAsync(AWeek);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var chips = Chips(view, "ChangeCategory");
            Assert.Equal(RecentChangesViewModel.Categories, chips.Select(c => (string)c.Content));

            foreach (var chip in chips)
            {
                chip.IsChecked = true;
                Assert.Equal((string)chip.Content, vm.SelectedCategory);
            }

            chips.Single(c => (string)c.Content == "Programs").IsChecked = true;
            Relayout(view);
            var shown = ShownTexts(Named<ItemsControl>(view, "Recent changes, by day"));
            Assert.Contains("New program: Search Pro Toolbar", shown);
            Assert.DoesNotContain("Turned on", shown);
        });
    }

    [Fact]
    public async Task EveryPeriodChip_SetsThePeriodItSays()
    {
        var vm = await VmAsync(AWeek);

        StaHelper.Run(() =>
        {
            var chips = Chips(Laid(vm), "ChangePeriod");
            Assert.Equal(RecentChangesViewModel.Periods, chips.Select(c => (string)c.Content));

            foreach (var chip in chips)
            {
                chip.IsChecked = true;
                Assert.Equal((string)chip.Content, vm.SelectedPeriod);
            }
        });
    }

    [Fact]
    public async Task TheToolbarButtons_AreNamed_AndRunTheirCommands()
    {
        var vm = await VmAsync(AWeek);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            Assert.Same(vm.RefreshCommand, Named<Button>(view, "Refresh the list of recent changes").Command);
            Assert.Same(vm.ExportCsvCommand, Named<Button>(view, "Export CSV — the changes shown").Command);
        });
    }

    private static List<RadioButton> Chips(DependencyObject root, string group) =>
        [.. Descendants<RadioButton>(root).Where(r => r.GroupName == group)];

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants<T>(root), e => AutomationProperties.GetName(e) == name);

    /// <summary>
    /// Whether nothing between <paramref name="element"/> and the view is collapsed. <c>IsVisible</c> cannot say: it is
    /// false for anything not shown in a window, and these views are laid out without one.
    /// </summary>
    private static bool Shown(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    /// <summary>The words actually on screen under <paramref name="root"/>, in tree order, icon glyphs left out.</summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block && !IsGlyph(block.Text)) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }

    private static bool IsGlyph(string text) => text.Length == 1 && text[0] is >= '\uE000' and <= '\uF8FF';

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
