// SysManager · TrayMenuTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The tray context menu's structure: what it contains, in what order, and what it omits.
/// </summary>
/// <remarks>
/// Integration rather than unit because a <see cref="ContextMenu"/> is a <c>DispatcherObject</c> and cannot
/// be constructed off an STA thread. It stops short of <c>TrayIconService.Initialize</c>, which calls
/// <c>ForceCreate</c> and registers an icon with the Windows notification area — the menu itself is ordinary
/// WPF and needs nothing from the shell.
/// <para>Order is asserted, not just membership. The CPU readout sits directly above "What's using my PC"
/// on purpose: the header says the CPU is at 80%, and the next item answers what is using it. Membership
/// alone would pass with the two separated, which is the whole design gone (#1589).</para>
/// </remarks>
public class TrayMenuTests
{
    /// <summary>Header text of every item in the menu, with separators marked.</summary>
    private static List<string> MenuShape(ContextMenu menu) =>
        [.. menu.Items.Cast<object>().Select(i => i switch
        {
            Separator => "---",
            MenuItem m => m.Header?.ToString() ?? "",
            _ => i.GetType().Name,
        })];

    private static void WithMenu(Action<string>? navigateToTab, Action<ContextMenu, TrayIconService> assert)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            using var svc = new TrayIconService(new SystemInfoService());
            // A Window is constructed but never shown: the menu items close over it to call ShowWindow, and
            // nothing here clicks them.
            var menu = svc.BuildContextMenu(new Window(), navigateToTab);
            assert(menu, svc);
        });
    }

    [Fact]
    public void TheMenu_OpensWithAStatusReadoutAboveTheJumps()
    {
        WithMenu(navId => { }, (menu, _) =>
        {
            var shape = MenuShape(menu);

            Assert.Equal(
            [
                "Reading system status…",
                "---",
                "Show SysManager",
                "What's using my PC",
                "Free up space",
                "Volume mixer",
                "---",
                "Exit",
            ], shape);
        });
    }

    [Fact]
    public void TheStatusHeader_IsNotClickable()
    {
        // A readout, not an action. Leaving it enabled would make it look pressable and promise something it
        // does not do — and it is the first thing the pointer lands on when the menu opens.
        WithMenu(navId => { }, (menu, _) =>
        {
            var header = Assert.IsType<MenuItem>(menu.Items[0]);

            Assert.False(header.IsEnabled);
            Assert.Equal("Reading system status…", header.Header);
        });
    }

    [Fact]
    public void WithNoNavigationCallback_TheJumpsAreAbsentRatherThanDead()
    {
        // The service is deliberately VM-agnostic: navigation arrives as a callback from the View layer. A
        // caller that supplies none must get a menu without the jumps, not three items that do nothing when
        // clicked. Nothing else pins this, and the old conditional "Volume mixer" had the same property by
        // accident rather than by test.
        WithMenu(navigateToTab: null, (menu, _) =>
        {
            var shape = MenuShape(menu);

            Assert.Equal(["Reading system status…", "---", "Show SysManager", "---", "Exit"], shape);
            Assert.DoesNotContain("What's using my PC", shape);
            Assert.DoesNotContain("Free up space", shape);
            Assert.DoesNotContain("Volume mixer", shape);
        });
    }

    [Fact]
    public void OpeningTheMenu_RefreshesTheStatusFromTheLatestPoll()
    {
        // The header is built once and the menu is opened many times, so the text is pulled on Opened rather
        // than the menu being rebuilt every 60-second tick. Without that, a menu built at startup would show
        // "Reading system status…" for the life of the process.
        WithMenu(navId => { }, (menu, svc) =>
        {
            var header = (MenuItem)menu.Items[0];
            Assert.Equal("Reading system status…", header.Header);

            // Stand in for a completed poll. UpdateTooltipAsync writes exactly this, from the same helper.
            svc.MenuStatusLine = "CPU 7%  ·  RAM 5.0/16.0 GB  ·  up 1d 2h";
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));

            Assert.Equal("CPU 7%  ·  RAM 5.0/16.0 GB  ·  up 1d 2h", header.Header);
        });
    }

    [Fact]
    public void EveryJumpItem_ComesFromTheOneQuickJumpsTable()
    {
        // The labels are plain-language and deliberately not the tab names, so a reader cannot check them
        // against the sidebar by eye — which is exactly how a label could end up with no destination.
        //
        // Asserted against the table the menu is BUILT from rather than by clicking the items: the click
        // handler calls ShowWindow, and showing a window is what a test must not do. This is the stronger
        // check anyway. If the menu's jump labels are the table's labels, in the table's order, then there is
        // no second list that could disagree with it, and the nav ids travelling with those labels are
        // covered by ArchitectureTests.EveryNavIdWrittenInTheApp_ResolvesToARealTab.
        WithMenu(navId => { }, (menu, _) =>
        {
            var shape = MenuShape(menu);
            var jumps = shape.Skip(3).Take(TrayIconService.QuickJumps.Length).ToList();

            Assert.Equal([.. TrayIconService.QuickJumps.Select(j => j.Label)], jumps);
        });
    }

    [Fact]
    public void TheQuickJumps_StayWithinTheStatedCeiling()
    {
        // Three is a documented ceiling, not an accident: the tray is where this app spends most of its
        // uptime and is the easiest surface in it to turn into clutter. A fourth item is a decision, so it
        // should break a test rather than be added quietly.
        Assert.Equal(3, TrayIconService.QuickJumps.Length);

        Assert.Equal(["nav-processes", "nav-cleanup", "nav-volume-control"],
            TrayIconService.QuickJumps.Select(j => j.NavId));

        // Process Manager first, directly under the CPU readout. The header says the CPU is at 80%; the next
        // item answers what is using it. Reordering breaks that, so the order is pinned above, not just the
        // membership.
        Assert.All(TrayIconService.QuickJumps, j =>
        {
            Assert.StartsWith("nav-", j.NavId, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(j.Label));
        });
    }
}
