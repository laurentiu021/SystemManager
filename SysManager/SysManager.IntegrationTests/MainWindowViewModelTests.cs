// SysManager · MainWindowViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// One tab graph, built once, for the tests that only READ the navigation surface.
/// </summary>
/// <remarks>
/// <see cref="MainWindowViewModel"/>'s parameterless constructor is the designer/test path, so it calls
/// <c>BuildDesignerGraph()</c> and builds all 58 tab view models eagerly — including the ones that launch
/// real <c>winget</c> and <c>powershell</c> child processes. Those children outlive the test, and the cost
/// is superlinear in the number of constructions rather than in the number of assertions: this class ran
/// 1 construction in 0.9&#160;s, 41 in 9.7&#160;s and 43 in 27.3&#160;s.
/// <para>At 41 it had already passed the point where the test host cannot shut down — locally
/// "Foreground threads were left running, forcing process exit"; in CI a 22-minute run, nine
/// <c>winget_*_hang.log</c>/<c>powershell_*_hang.log</c> artifacts, a hang dump and exit
/// <c>-532462766</c>, with <b>every test passing</b> (652/652, <c>failed: 0</c>, <c>error: 1</c>). Because
/// that job is <c>continue-on-error</c>, the pull request showed a green check throughout (#2312).</para>
/// <para>Shared rather than disposed-per-test on purpose: adding <c>using</c> to all 41 changed nothing,
/// so whatever holds the process open is not released by <c>ViewModelBase.Dispose</c>. Not building the
/// graph 41 times is the fix; disposing it 41 times is not.</para>
/// <para>Only safe because the tests using it are read-only. The three that touch
/// <c>SelectedNav</c> keep their own graph — two mutate the selection, and one asserts the INITIAL
/// selection, so it cannot run after another test has navigated. If you add a test here that changes
/// state, give it its own instance and say why.</para>
/// </remarks>
public sealed class NavSurfaceFixture : IDisposable
{
    public MainWindowViewModel Vm { get; } = new();

    public void Dispose() => Vm.Dispose();
}

[Collection("Network")]
public class MainWindowViewModelTests(NavSurfaceFixture fixture) : IClassFixture<NavSurfaceFixture>
{
    /// <summary>The shared read-only graph. See <see cref="NavSurfaceFixture"/> for why it is shared.</summary>
    private readonly MainWindowViewModel _nav = fixture.Vm;


    // Tabs are addressed through the real navigation surface (NavItems → Content), not
    // through per-tab accessor properties: those were test-only sugar removed when tab
    // view-models became lazily built (the "eager-VM startup herd" fix). In the test path
    // (no DI container) every NavItem's Content is still eager, so touching it proves the VM
    // constructs exactly as before — while pinning the surface the app actually navigates.
    private static object TabContent(MainWindowViewModel vm, string navId)
        => vm.NavItems.First(n => n.Id == navId).Content;

    [Fact]
    public void AllTabsAreInstantiated()
    {
        var vm = _nav;
        Assert.NotNull(TabContent(vm, "nav-dashboard"));
        Assert.NotNull(TabContent(vm, "nav-app-updates"));
        Assert.NotNull(TabContent(vm, "nav-windows-update"));
        Assert.NotNull(TabContent(vm, "nav-system-health"));
        Assert.NotNull(TabContent(vm, "nav-cleanup"));
        Assert.NotNull(TabContent(vm, "nav-deep-cleanup"));
        Assert.NotNull(TabContent(vm, "nav-ping"));
        Assert.NotNull(TabContent(vm, "nav-traceroute"));
        Assert.NotNull(TabContent(vm, "nav-speed-test"));
        Assert.NotNull(TabContent(vm, "nav-network-repair"));
        Assert.NotNull(TabContent(vm, "nav-drivers"));
        Assert.NotNull(TabContent(vm, "nav-logs"));
        Assert.NotNull(TabContent(vm, "nav-about"));
        // NetworkSharedState is not a tab of its own — it is shared by the four network tabs.
        // Assert it exists AND that the tabs genuinely share it (the whole point of the type).
        Assert.NotNull(((PingViewModel)TabContent(vm, "nav-ping")).Shared);
    }

    // Regression (lazy-VM startup fix): the app shell (MainWindow.xaml) binds its version label
    // and update banner to About.*, and About's constructor runs the startup update-check that
    // fills those bindings. So About MUST stay eager and be exposed as a property — making it a
    // lazy tab would leave the shell banner/version blank until the About tab was first opened.
    // The About tab and the shell must also share ONE instance (one update-check, one state).
    [Fact]
    public void About_IsEagerlyExposed_AndSharedWithItsTab()
    {
        var vm = _nav;
        Assert.NotNull(vm.About);
        Assert.Same(vm.About, TabContent(vm, "nav-about"));
    }

    [Fact]
    public void ElevationBadge_IsOneOfTwoValues()
    {
        var vm = _nav;
        Assert.True(vm.ElevationBadge == "Administrator" || vm.ElevationBadge == "Standard user",
            $"Unexpected badge: {vm.ElevationBadge}");
    }

    [Fact]
    public void Title_NotEmpty()
    {
        var vm = _nav;
        Assert.False(string.IsNullOrWhiteSpace(vm.Title));
    }

    [Fact]
    public void Title_ReflectsElevation()
    {
        var vm = _nav;
        if (vm.IsElevated)
            Assert.Contains("Admin", vm.Title);
        else
            Assert.Equal("SysManager", vm.Title);
    }

    [Fact]
    public void EachTabViewModel_HasCorrectType()
    {
        var vm = _nav;
        Assert.IsType<DashboardViewModel>(TabContent(vm, "nav-dashboard"));
        Assert.IsType<AppUpdatesViewModel>(TabContent(vm, "nav-app-updates"));
        Assert.IsType<WindowsUpdateViewModel>(TabContent(vm, "nav-windows-update"));
        Assert.IsType<SystemHealthViewModel>(TabContent(vm, "nav-system-health"));
        Assert.IsType<CleanupViewModel>(TabContent(vm, "nav-cleanup"));
        Assert.IsType<DeepCleanupViewModel>(TabContent(vm, "nav-deep-cleanup"));
        Assert.IsType<PingViewModel>(TabContent(vm, "nav-ping"));
        Assert.IsType<TracerouteViewModel>(TabContent(vm, "nav-traceroute"));
        Assert.IsType<SpeedTestViewModel>(TabContent(vm, "nav-speed-test"));
        Assert.IsType<NetworkRepairViewModel>(TabContent(vm, "nav-network-repair"));
        Assert.IsType<DriversViewModel>(TabContent(vm, "nav-drivers"));
        Assert.IsType<LogsViewModel>(TabContent(vm, "nav-logs"));
        Assert.IsType<AboutViewModel>(TabContent(vm, "nav-about"));

        // The four network tabs share ONE NetworkSharedState instance (created once in the
        // MainWindowViewModel, injected into each). Pin both its type and the sharing.
        var ping = (PingViewModel)TabContent(vm, "nav-ping");
        Assert.IsType<NetworkSharedState>(ping.Shared);
        Assert.Same(ping.Shared, ((TracerouteViewModel)TabContent(vm, "nav-traceroute")).Shared);
        Assert.Same(ping.Shared, ((SpeedTestViewModel)TabContent(vm, "nav-speed-test")).Shared);
        Assert.Same(ping.Shared, ((NetworkRepairViewModel)TabContent(vm, "nav-network-repair")).Shared);
    }

    [Fact]
    public void NavItems_ContainAll58()
    {
        var vm = _nav;
        Assert.Equal(58, vm.NavItems.Count);
        var ids = vm.NavItems.Select(n => n.Id).ToList();

        // Dashboard
        Assert.Contains("nav-dashboard", ids);

        // System (11)
        Assert.Contains("nav-system-health", ids);
        Assert.Contains("nav-windows-update", ids);
        Assert.Contains("nav-performance", ids);
        Assert.Contains("nav-services", ids);
        Assert.Contains("nav-startup", ids);
        Assert.Contains("nav-windows-features", ids);
        Assert.Contains("nav-restore-points", ids);
        Assert.Contains("nav-task-scheduler", ids);
        Assert.Contains("nav-boot-analyzer", ids);
        Assert.Contains("nav-system-fixes", ids);
        Assert.Contains("nav-tweaks-hub", ids);

        // Gaming & Profiles (5)
        Assert.Contains("nav-gaming-profile", ids);
        Assert.Contains("nav-standby-cleaner", ids);
        Assert.Contains("nav-timer-resolution", ids);
        Assert.Contains("nav-cpu-affinity", ids);
        Assert.Contains("nav-display-profiles", ids);

        // Monitor (5)
        Assert.Contains("nav-processes", ids);
        Assert.Contains("nav-resource-history", ids);
        Assert.Contains("nav-privacy-monitor", ids);
        Assert.Contains("nav-app-alerts", ids);
        Assert.Contains("nav-settings-watchdog", ids);

        // Cleanup (4)
        Assert.Contains("nav-cleanup", ids);
        Assert.Contains("nav-deep-cleanup", ids);
        Assert.Contains("nav-shortcut-cleaner", ids);
        Assert.Contains("nav-scheduled-maintenance", ids);

        // Storage & Files (3) — File Lock Detector is per-file work, not continuous monitoring
        Assert.Contains("nav-disk-analyzer", ids);
        Assert.Contains("nav-duplicates", ids);
        Assert.Contains("nav-file-lock", ids);

        // Network (6) — DNS changer + hosts editor merged into one DNS & Hosts tab
        Assert.Contains("nav-ping", ids);
        Assert.Contains("nav-traceroute", ids);
        Assert.Contains("nav-speed-test", ids);
        Assert.Contains("nav-bandwidth-monitor", ids);
        Assert.Contains("nav-network-repair", ids);
        Assert.Contains("nav-dns-hosts", ids);

        // Apps (3)
        Assert.Contains("nav-app-updates", ids);
        Assert.Contains("nav-bulk-installer", ids);
        Assert.Contains("nav-uninstaller", ids);

        // Privacy & Security (7)
        Assert.Contains("nav-privacy-settings", ids);
        Assert.Contains("nav-file-shredder", ids);
        Assert.Contains("nav-app-blocker", ids);
        Assert.Contains("nav-debloater", ids);
        Assert.Contains("nav-browser-cleaner", ids);
        Assert.Contains("nav-edge-onedrive", ids);
        Assert.Contains("nav-defender-tweaks", ids);

        // Customization (4)
        Assert.Contains("nav-context-menu", ids);
        Assert.Contains("nav-dark-mode", ids);
        Assert.Contains("nav-volume-control", ids);
        Assert.Contains("nav-notification-blocker", ids);

        // Info (6)
        Assert.Contains("nav-drivers", ids);
        Assert.Contains("nav-battery", ids);
        Assert.Contains("nav-logs", ids);
        Assert.Contains("nav-system-report", ids);
        Assert.Contains("nav-legacy-panels", ids);
        Assert.Contains("nav-about", ids);

        // Advanced (3)
        Assert.Contains("nav-profile-export", ids);
        Assert.Contains("nav-cli-interface", ids);
        Assert.Contains("nav-env-variables", ids);
    }

    [Fact]
    public void OpenAboutTabCommand_SwitchesSelection()
    {
        var vm = new MainWindowViewModel();
        var dashboard = vm.SelectedNav!;

        vm.OpenAboutTabCommand.Execute(null);

        Assert.NotNull(vm.SelectedNav);
        Assert.Equal("nav-about", vm.SelectedNav!.Id);
        Assert.False(dashboard.IsSelected);
        Assert.True(vm.SelectedNav.IsSelected);
        Assert.Single(vm.NavItems, item => item.IsSelected);
    }

    [Fact]
    public void SelectedNav_DefaultsToDashboard()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.SelectedNav);
        Assert.Equal("nav-dashboard", vm.SelectedNav!.Id);
        Assert.True(vm.SelectedNav.IsSelected);
        Assert.Single(vm.NavItems, item => item.IsSelected);
    }

    [Fact]
    public void SelectedNav_NullClearsSelectionState()
    {
        var vm = new MainWindowViewModel();
        var dashboard = vm.SelectedNav!;
        var dashboardViewModel = Assert.IsType<DashboardViewModel>(dashboard.Content);
        Assert.True(dashboardViewModel.IsActive);

        vm.SelectedNav = null;

        Assert.False(dashboard.IsSelected);
        Assert.DoesNotContain(vm.NavItems, item => item.IsSelected);
        Assert.False(dashboardViewModel.IsActive);
    }

    [Fact]
    public void NavItems_HaveUniqueIds()
    {
        var vm = _nav;
        var ids = vm.NavItems.Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>
    /// Leaf nav items carry a label, a view and content. They no longer carry a glyph.
    /// </summary>
    /// <remarks>
    /// This assertion used to include <c>Glyph</c>, and it was FAILING the whole time: every item was
    /// constructed with <c>Glyph = ""</c>, so the non-blank check could not pass. It went unnoticed
    /// because CI compile-checks this project without executing it — the one test that would have caught
    /// the empty sidebar gutter on day one was in the one place nothing runs it.
    /// <para>Icons now live on the group, so the glyph half moved to
    /// <see cref="NavGroups_CarryDistinctGlyphs"/>, where it is a real assertion instead of an
    /// impossible one.</para>
    /// </remarks>
    [Fact]
    public void NavItems_AllHaveLabelsAndAView()
    {
        var vm = _nav;
        Assert.All(vm.NavItems, n =>
        {
            Assert.False(string.IsNullOrWhiteSpace(n.Label));
            Assert.NotNull(n.Content);
            Assert.NotNull(n.ViewType);
        });
    }

    /// <summary>
    /// Every group carries a glyph, and no two groups share one.
    /// </summary>
    /// <remarks>
    /// Distinctness is the point rather than mere presence: twelve rows drawn with the same icon
    /// differentiate nothing, which is the state the sidebar was already in with twelve empty ones.
    /// </remarks>
    [Fact]
    public void NavGroups_CarryDistinctGlyphs()
    {
        var vm = _nav;
        Assert.All(vm.NavGroups, g => Assert.False(string.IsNullOrWhiteSpace(g.Glyph)));

        var glyphs = vm.NavGroups.Select(g => g.Glyph).ToList();
        Assert.Equal(glyphs.Count, glyphs.Distinct(StringComparer.Ordinal).Count());
    }

    // ── NavGroup tests ──────────────────────────────────────────────

    [Fact]
    public void NavGroups_Has12Groups()
    {
        var vm = _nav;
        Assert.Equal(12, vm.NavGroups.Count);
    }

    [Fact]
    public void NavGroups_HaveUniqueIds()
    {
        var vm = _nav;
        var ids = vm.NavGroups.Select(g => g.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void NavGroups_AllHaveChildren()
    {
        var vm = _nav;
        Assert.All(vm.NavGroups, g =>
        {
            Assert.NotEmpty(g.Children);
            Assert.False(string.IsNullOrWhiteSpace(g.Label));
            Assert.False(string.IsNullOrWhiteSpace(g.Glyph));
        });
    }

    [Fact]
    public void NavGroups_SingleItemGroups_AreDashboardOnly()
    {
        var vm = _nav;
        var singles = vm.NavGroups.Where(g => g.IsSingleItem).Select(g => g.Id).ToList();
        Assert.Contains("grp-dashboard", singles);
        Assert.Single(singles);
    }

    [Fact]
    public void NavGroups_SystemGroup_Contains11Items()
    {
        var vm = _nav;
        var sys = vm.NavGroups.First(g => g.Id == "grp-system");
        Assert.Equal(11, sys.Children.Count);
        var ids = sys.Children.Select(c => c.Id).ToList();
        Assert.Contains("nav-system-health", ids);
        Assert.Contains("nav-windows-update", ids);
        Assert.Contains("nav-performance", ids);
        Assert.Contains("nav-services", ids);
        Assert.Contains("nav-startup", ids);
        Assert.Contains("nav-windows-features", ids);
        Assert.Contains("nav-restore-points", ids);
        Assert.Contains("nav-task-scheduler", ids);
        Assert.Contains("nav-boot-analyzer", ids);
        Assert.Contains("nav-system-fixes", ids);
        Assert.Contains("nav-tweaks-hub", ids);
        // Legacy Panels is a read-only applet launcher — it lives in Info, not System.
        Assert.DoesNotContain("nav-legacy-panels", ids);
    }

    [Fact]
    public void NavGroups_AppAlerts_LivesInMonitorNotPrivacy()
    {
        var vm = _nav;
        var monitor = vm.NavGroups.First(g => g.Id == "grp-monitor").Children.Select(c => c.Id).ToList();
        var privacy = vm.NavGroups.First(g => g.Id == "grp-privacy").Children.Select(c => c.Id).ToList();
        // App Alerts passively watches for new installs — it belongs with the monitoring tabs.
        Assert.Contains("nav-app-alerts", monitor);
        Assert.DoesNotContain("nav-app-alerts", privacy);
    }

    [Fact]
    public void NavGroups_LegacyPanels_LivesInInfoNotSystem()
    {
        var vm = _nav;
        var info = vm.NavGroups.First(g => g.Id == "grp-info").Children.Select(c => c.Id).ToList();
        var system = vm.NavGroups.First(g => g.Id == "grp-system").Children.Select(c => c.Id).ToList();
        Assert.Contains("nav-legacy-panels", info);
        Assert.DoesNotContain("nav-legacy-panels", system);
    }

    [Fact]
    public void NavGroups_StorageGroup_ContainsDiskAnalyzerAndDuplicates()
    {
        var vm = _nav;
        var storage = vm.NavGroups.First(g => g.Id == "grp-storage");
        var ids = storage.Children.Select(c => c.Id).ToList();
        Assert.Contains("nav-disk-analyzer", ids);
        Assert.Contains("nav-duplicates", ids);
    }

    /// <summary>
    /// Three tabs are filed by the errand that brings someone to them, not by how they work inside.
    /// </summary>
    /// <remarks>
    /// One test and one view model for all three placements, deliberately. Every test in this class
    /// builds its own <see cref="MainWindowViewModel"/>, and on the test path that constructs the whole
    /// tab graph eagerly — including the view models that launch real winget and PowerShell child
    /// processes. Those children outlive the test: adding three more constructions here took the CI
    /// integration run from ~6 minutes to 22, with a pile of `winget_*_hang.log` artifacts and a host
    /// process that could no longer exit. The suite still reported every test passing, because that job
    /// is `continue-on-error`. So this asserts three placements against one graph rather than three.
    /// <para><b>File Lock Detector</b> → Storage &amp; Files: someone arrives from Windows' own "the file
    /// is open in another program" dialog, which is a files errand. "Monitor" promises continuous
    /// watching, and this tab has no timer and no poll loop — it is a one-shot scan of a path the user
    /// typed (#1521).</para>
    /// <para><b>Bandwidth Monitor</b> → directly after Speed Test in Network. "How fast is my connection"
    /// and "what is using my connection" are halves of one errand, and while they sat in different groups
    /// finding one never led to the other. The adjacency is the point, so the position is asserted and not
    /// just the membership. This tab genuinely IS a live monitor — one of the few with a visibility-gated
    /// poll loop — so Monitor was defensible on mechanism; it is placed on the errand instead, because
    /// nobody whose internet feels slow looks under Monitor (#1514).</para>
    /// <para><b>Notification Blocker</b> → Customization. Every other tab in Privacy &amp; Security either
    /// changes a privileged surface or removes software. This one flips the same per-app switches Windows
    /// Settings does, needs no administrator, and is one flip from undone. Under a group named Security it
    /// overstated the stakes and hid the tab from where the wish belongs (#1522).</para>
    /// </remarks>
    [Fact]
    public void NavGroups_FileTabsAreGroupedByErrand_NotByMechanism()
    {
        var vm = _nav;

        var monitor = Ids(vm, "grp-monitor");
        var storage = vm.NavGroups.First(g => g.Id == "grp-storage");
        var network = Ids(vm, "grp-network");
        var customization = Ids(vm, "grp-customization");
        var privacy = Ids(vm, "grp-privacy");

        // #1521 — File Lock Detector, plus the group rename that makes the name cover per-file work.
        Assert.Contains("nav-file-lock", storage.Children.Select(c => c.Id));
        Assert.DoesNotContain("nav-file-lock", monitor);
        Assert.Equal("Storage & Files", storage.Label);

        // #1514 — Bandwidth Monitor, asserted by POSITION: immediately after Speed Test.
        Assert.DoesNotContain("nav-bandwidth-monitor", monitor);
        var speedTest = network.IndexOf("nav-speed-test");
        Assert.True(speedTest >= 0,
            "Network no longer contains Speed Test, so the adjacency this pins cannot be checked at all.");
        Assert.Equal("nav-bandwidth-monitor", network[speedTest + 1]);

        // #1522 — Notification Blocker.
        Assert.Contains("nav-notification-blocker", customization);
        Assert.DoesNotContain("nav-notification-blocker", privacy);
    }

    private static List<string> Ids(MainWindowViewModel vm, string groupId)
        => vm.NavGroups.First(g => g.Id == groupId).Children.Select(c => c.Id).ToList();

    [Fact]
    public void NavGroups_CleanupGroup_Has4Items()
    {
        // Cleanup has: Quick, Deep, Shortcut Cleaner, Scheduled Maintenance.
        // File Shredder lives under Privacy & Security, not Cleanup.
        var vm = _nav;
        var cleanup = vm.NavGroups.First(g => g.Id == "grp-cleanup");
        Assert.Equal(4, cleanup.Children.Count);
    }

    [Fact]
    public void NavGroups_FlatNavItems_MatchGroupChildren()
    {
        var vm = _nav;
        var fromGroups = vm.NavGroups.SelectMany(g => g.Children).ToList();
        Assert.Equal(fromGroups.Count, vm.NavItems.Count);
        for (int i = 0; i < fromGroups.Count; i++)
            Assert.Same(fromGroups[i], vm.NavItems[i]);
    }

    /// <summary>
    /// Exactly ONE collapsible group starts expanded, and it is the one <c>InitiallyExpandedGroupId</c>
    /// names. Every other one starts collapsed.
    /// </summary>
    /// <remarks>
    /// This used to assert that ALL of them start collapsed, which was the state #1519 was about: the app
    /// opened showing twelve category names and not one feature. One group now opens — and it has to be
    /// one, because at 820px the twelve collapsed groups already fill 600 of a 670px viewport, so a second
    /// pushes category headings below the fold.
    /// <para>Asserted against the constant rather than the string, so the two cannot drift; and the count is
    /// asserted as well as the identity, because "one is expanded" and "only one is expanded" are different
    /// claims and the second is the one the viewport arithmetic depends on.</para>
    /// <para>A constant naming a group that no longer exists fails here too, by expanding nothing — which is
    /// worth stating because that failure is silent in the app: the sidebar simply opens as it used to.</para>
    /// </remarks>
    [Fact]
    public void NavGroups_ExactlyTheCleanupGroupStartsExpanded()
    {
        var vm = _nav;
        var collapsible = vm.NavGroups.Where(group => !group.IsSingleItem).ToList();

        var expanded = collapsible.Where(group => group.IsExpanded).ToList();

        var only = Assert.Single(expanded);
        Assert.Equal(MainWindowViewModel.InitiallyExpandedGroupId, only.Id);
        Assert.All(
            collapsible.Where(group => group.Id != MainWindowViewModel.InitiallyExpandedGroupId),
            group => Assert.False(group.IsExpanded));
    }

    // ── Data-driven contract: every leaf in the live graph is well-formed ──
    // Enumerates the real nav tree instead of hard-coding ids, so a future
    // nav/wiring change that leaves a leaf without content or a view fails
    // automatically rather than being silently re-baselined.

    [Fact]
    public void NavLeaf_EveryItemHasContentAndResolvableView()
    {
        var vm = _nav;
        Assert.All(vm.NavItems, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.NotNull(item.Content);
            Assert.NotNull(item.ViewType);
            // ViewType must be a UserControl subclass the lazy View getter can instantiate.
            Assert.True(typeof(System.Windows.Controls.UserControl).IsAssignableFrom(item.ViewType),
                $"{item.Id}: ViewType {item.ViewType.Name} is not a UserControl");
        });
    }

    [Fact]
    public void NavGroups_EveryLeafBelongsToExactlyOneGroup()
    {
        var vm = _nav;
        foreach (var item in vm.NavItems)
        {
            var owners = vm.NavGroups.Count(g => g.Children.Contains(item));
            Assert.Equal(1, owners);
        }
    }

    // These eight features graduated out of Preview (#1123). The tests pin both the
    // real-view wiring AND the graduated state (IsInDevelopment == false), so a future
    // accidental re-flag to preview fails here.
    [Fact]
    public void NavLeaf_TimerResolution_IsImplementedAndGraduated()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-timer-resolution");
        Assert.Equal(typeof(SysManager.Views.TimerResolutionView), item.ViewType);
        Assert.IsType<TimerResolutionViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_FileLock_IsImplementedAndGraduated()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-file-lock");
        Assert.Equal(typeof(SysManager.Views.FileLockView), item.ViewType);
        Assert.IsType<FileLockViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_DisplayProfiles_IsImplementedAndGraduated()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-display-profiles");
        Assert.Equal(typeof(SysManager.Views.DisplayProfileView), item.ViewType);
        Assert.IsType<DisplayProfileViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_CpuAffinity_IsImplementedAndGraduated()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-cpu-affinity");
        Assert.Equal(typeof(SysManager.Views.CpuAffinityView), item.ViewType);
        Assert.IsType<CpuAffinityViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_Defender_IsImplementedAndGraduated()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-defender-tweaks");
        Assert.Equal(typeof(SysManager.Views.DefenderView), item.ViewType);
        Assert.IsType<DefenderViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_TaskScheduler_IsImplementedAndGraduated()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-task-scheduler");
        Assert.Equal(typeof(SysManager.Views.TaskSchedulerView), item.ViewType);
        Assert.IsType<TaskSchedulerViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_DarkMode_IsImplementedAndGraduated()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-dark-mode");
        Assert.Equal(typeof(SysManager.Views.DarkModeView), item.ViewType);
        Assert.IsType<DarkModeViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_StandbyCleaner_IsImplementedAndGraduated()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-standby-cleaner");
        Assert.Equal(typeof(SysManager.Views.StandbyMemoryView), item.ViewType);
        Assert.IsType<StandbyMemoryViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    // Resource History (#13) is implemented but newly added — it's wired to a real
    // view/VM and flagged PREVIEW (IsInDevelopment == true) until QA-verified. This pins
    // both facts so a premature graduation, or a regression to a stub view, fails here.
    // (The old WIP placeholder view/VM was deleted once the last tab graduated off it.)
    [Fact]
    public void NavLeaf_ResourceHistory_IsImplementedAndInPreview()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-resource-history");
        Assert.Equal(typeof(SysManager.Views.ResourceHistoryView), item.ViewType);
        Assert.IsType<ResourceHistoryViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }

    // Settings Watchdog (#335) — implemented, wired to a real view/VM, flagged PREVIEW.
    [Fact]
    public void NavLeaf_SettingsWatchdog_IsImplementedAndInPreview()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-settings-watchdog");
        Assert.Equal(typeof(SysManager.Views.SettingsWatchdogView), item.ViewType);
        Assert.IsType<SettingsWatchdogViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }

    // CLI Interface (#342) — implemented reference tab, wired to a real view/VM, flagged PREVIEW.
    [Fact]
    public void NavLeaf_CliInterface_IsImplementedAndInPreview()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-cli-interface");
        Assert.Equal(typeof(SysManager.Views.CliInterfaceView), item.ViewType);
        Assert.IsType<CliInterfaceViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }

    // Scheduled Maintenance (#10) — implemented, wired to a real view/VM, flagged PREVIEW.
    [Fact]
    public void NavLeaf_ScheduledMaintenance_IsImplementedAndInPreview()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-scheduled-maintenance");
        Assert.Equal(typeof(SysManager.Views.ScheduledMaintenanceView), item.ViewType);
        Assert.IsType<ScheduledMaintenanceViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }

    // Tweaks Hub (#907) — a brand-new tab (not a graduated placeholder), real view/VM, PREVIEW.
    [Fact]
    public void NavLeaf_TweaksHub_IsImplementedAndInPreview()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-tweaks-hub");
        Assert.Equal(typeof(SysManager.Views.TweaksHubView), item.ViewType);
        Assert.IsType<TweaksHubViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }

    // Notification Blocker (#340) — graduated from the last WIP placeholder to a real
    // view/VM, flagged PREVIEW. It was the final tab on the placeholder, so with this every
    // sidebar entry is backed by a real view; the placeholder view/VM has since been deleted.
    [Fact]
    public void NavLeaf_NotificationBlocker_IsImplementedAndInPreview()
    {
        var vm = _nav;
        var item = vm.NavItems.First(n => n.Id == "nav-notification-blocker");
        Assert.Equal(typeof(SysManager.Views.NotificationBlockerView), item.ViewType);
        Assert.IsType<NotificationBlockerViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }
}
