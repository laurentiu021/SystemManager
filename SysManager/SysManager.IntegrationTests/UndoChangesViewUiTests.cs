// SysManager · UndoChangesViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Undo Changes really draws what it found (#1525): each row's words and button, the pill on a row that needs
/// administrator rights, the empty state, the links to the tabs whose switches are their own undo, and the restore
/// point line.
/// </summary>
/// <remarks>
/// The unit tests prove the view model builds the rows and the words. They cannot prove the view shows them: a binding
/// on the wrong path compiles and draws nothing. So this loads the real XAML on the STA thread with the app's
/// dictionaries, as <see cref="BrowserCleanerViewUiTests"/> does. The service is a fake written here, so nothing on
/// this PC is read or put back.
/// </remarks>
[Collection("Network")]
public sealed class UndoChangesViewUiTests
{
    private sealed class FakeUndo(UndoScan scan) : IUndoChangesService
    {
        public Task<UndoScan> ScanAsync(CancellationToken ct = default) => Task.FromResult(scan);

        public Task<RestorePointLook> LookForRestorePointAsync(CancellationToken ct = default) =>
            Task.FromResult(new RestorePointLook(null, Listed: false));

        public Task<UndoOutcome> PutBackAsync(UndoChange change, CancellationToken ct = default) =>
            Task.FromResult(new UndoOutcome(UndoOutcomeKind.NothingLeft, ""));
    }

    private sealed class NoNavigation : INavigationService
    {
        public void GoTo(string navId, string? filter = null) { }
    }

    private static readonly UndoChange Performance = new(
        UndoChangeKind.PerformanceMode, "Performance Mode", "Changed the power plan and visual effects.",
        "First changed 5 Oct 2026, 19:40", NeedsAdmin: false, "Put back…",
        ["Power plan: Ultimate Performance → Balanced"], "Put back Performance Mode?", "Put back Performance Mode — Confirm");

    private static readonly UndoChange Hosts = new(
        UndoChangeKind.HostsFile, "Hosts file", "The hosts file was changed. The copy from before the first change is kept.",
        "Copy from 28 Sep 2026, 10:05", NeedsAdmin: true, "Restore the copy…", [], "Restore the hosts file?",
        "Restore the hosts file — Confirm");

    private static async Task<UndoChangesViewModel> VmAsync(params UndoChange[] changes)
    {
        var vm = new UndoChangesViewModel(new FakeUndo(new UndoScan(changes, [])), new NoNavigation());
        await vm.InitializationComplete;
        return vm;
    }

    private static UndoChangesView Laid(UndoChangesViewModel vm)
    {
        AppResources.Ensure();
        var view = new UndoChangesView { DataContext = vm };
        view.Measure(new Size(1200, 2400));
        view.Arrange(new Rect(0, 0, 1200, 2400));
        view.UpdateLayout();
        return view;
    }

    [Fact]
    public async Task EachRow_ShowsItsWords_AndItsButton()
    {
        var vm = await VmAsync(Performance, Hosts);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var list = Named<ItemsControl>(view, "Changes SysManager can put back");
            var shown = ShownTexts(list);

            Assert.Contains("Performance Mode", shown);
            Assert.Contains("Changed the power plan and visual effects.", shown);
            Assert.Contains("First changed 5 Oct 2026, 19:40", shown);
            Assert.Contains("Hosts file", shown);
            Assert.Contains("Copy from 28 Sep 2026, 10:05", shown);

            var putBack = Named<Button>(view, "Put back — Performance Mode");
            Assert.Equal("Put back…", putBack.Content);
            Assert.Same(vm.PutBackCommand, putBack.Command);
            Assert.Same(vm.Changes[0], putBack.CommandParameter);
            Assert.True(putBack.IsEnabled);

            // Enabled exactly when the row can be acted on, which for this one depends on how SysManager runs.
            var restore = Named<Button>(view, "Restore the copy — Hosts file");
            Assert.Equal(vm.Changes[1].CanAct, restore.IsEnabled);
            Assert.Equal(vm.Changes[1].ButtonHint, AutomationProperties.GetHelpText(restore));
        });
    }

    [Fact]
    public async Task OnlyARowThatNeedsAdministratorRights_CarriesThePill()
    {
        var vm = await VmAsync(Performance, Hosts);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var rows = Descendants<Border>(Named<ItemsControl>(view, "Changes SysManager can put back"))
                .Where(b => b.Style == Application.Current.FindResource("ListRowBorder"))
                .ToList();

            Assert.Equal(2, rows.Count);
            Assert.DoesNotContain("Needs administrator", ShownTexts(rows[0]));
            Assert.Contains("Needs administrator", ShownTexts(rows[1]));
            // Each row carries its tab's sidebar group icon: System for Performance Mode, Network for the hosts file.
            Assert.Contains("\uE770", ShownGlyphs(rows[0]));
            Assert.Contains("\uE968", ShownGlyphs(rows[1]));
        });
    }

    [Fact]
    public async Task ARowThatNeedsAdministratorRights_HasItsButtonOff_ForAStandardUser_AndSaysWhy()
    {
        // The elevated machine running the suite never draws this state on its own, so it is forced here.
        using var standard = AdminHelper.ForceElevation(false);
        var vm = await VmAsync(Performance, Hosts);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var restore = Named<Button>(view, "Restore the copy — Hosts file");

            Assert.False(restore.IsEnabled);
            Assert.Equal("Needs administrator rights. Use Run as administrator at the top of the page.",
                AutomationProperties.GetHelpText(restore));
            Assert.True(ToolTipService.GetShowOnDisabled(restore));
            Assert.True(Named<Button>(view, "Put back — Performance Mode").IsEnabled);
        });
    }

    [Fact]
    public async Task NothingToPutBack_ShowsTheEmptyState_AndNoList()
    {
        var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var shown = ShownTexts(view);

            Assert.Contains("Nothing to put back", shown);
            Assert.Contains("Nothing SysManager changed is waiting to be put back. The sections below still apply.", shown);
            Assert.False(Shown(Named<ItemsControl>(view, "Changes SysManager can put back")));
        });
    }

    [Fact]
    public async Task WithRows_TheEmptyStateIsNotShown()
    {
        var vm = await VmAsync(Performance);

        StaHelper.Run(() => Assert.DoesNotContain("Nothing to put back", ShownTexts(Laid(vm))));
    }

    [Fact]
    public async Task EverySwitchLink_OpensItsTab_ThroughTheTabsCommand()
    {
        var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            foreach (var link in vm.Switches)
            {
                var open = Named<Button>(view, link.ButtonName);
                Assert.Equal("Open", open.Content);
                Assert.Same(vm.OpenTabCommand, open.Command);
                Assert.Equal(link.NavId, open.CommandParameter);
            }
        });
    }

    [Fact]
    public async Task TheWholePcCard_ShowsTheRestorePointLine_AndOpensRestorePoints()
    {
        var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            Assert.Contains(vm.RestorePointText, ShownTexts(view));
            var open = Named<Button>(view, "Open Restore Points");
            Assert.Same(vm.OpenTabCommand, open.Command);
            Assert.Equal("nav-restore-points", open.CommandParameter);
        });
    }

    [Fact]
    public async Task WhatCannotBePutBack_IsSaidPlainly()
    {
        var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid(vm));

            Assert.Contains("What cannot be put back", shown);
            Assert.Contains("• Files that were deleted for good, and anything File Shredder removed. Files sent to the Recycle "
                + "Bin can be put back from the Recycle Bin.", shown);
            Assert.Contains("• Preinstalled apps that were removed — most can be reinstalled from the Microsoft Store; ones "
                + "Microsoft has retired, such as Skype, cannot.", shown);
            Assert.Contains("• Programs removed with the Uninstaller — reinstall them.", shown);
        });
    }

    [Fact]
    public async Task AScreenReader_HearsEachRowAndLinkByItsWords()
    {
        var vm = await VmAsync(Performance, Hosts);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            Assert.Equal(
            [
                "Performance Mode. Changed the power plan and visual effects. First changed 5 Oct 2026, 19:40.",
                "Hosts file. The hosts file was changed. The copy from before the first change is kept. Copy from 28 Sep "
                    + "2026, 10:05. Needs administrator rights.",
            ], Names(Named<ItemsControl>(view, "Changes SysManager can put back")));
            Assert.Equal(vm.Switches.Select(s => s.ToString()).ToList(),
                Names(Named<ItemsControl>(view, "Tabs whose switches are their own undo")));
        });
    }

    /// <summary>What a screen reader is told each item of <paramref name="list"/> is called.</summary>
    private static List<string> Names(ItemsControl list) =>
        [.. (UIElementAutomationPeer.CreatePeerForElement(list).GetChildren() ?? []).Select(p => p.GetName())];

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

    private static List<string> ShownTexts(DependencyObject root) => Shown(root, glyphs: false);

    private static List<string> ShownGlyphs(DependencyObject root) => Shown(root, glyphs: true);

    /// <summary>The text actually on screen under <paramref name="root"/>, in tree order: words, or icon glyphs.</summary>
    private static List<string> Shown(DependencyObject root, bool glyphs)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block && IsGlyph(block.Text) == glyphs) texts.Add(block.Text);
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
