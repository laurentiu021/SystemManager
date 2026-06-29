// SysManager · MainWindowViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class MainWindowViewModelTests
{
    [Fact]
    public void AllTabsAreInstantiated()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.Dashboard);
        Assert.NotNull(vm.AppUpdates);
        Assert.NotNull(vm.WindowsUpdate);
        Assert.NotNull(vm.SystemHealth);
        Assert.NotNull(vm.Cleanup);
        Assert.NotNull(vm.DeepCleanup);
        Assert.NotNull(vm.NetworkShared);
        Assert.NotNull(vm.Ping);
        Assert.NotNull(vm.Traceroute);
        Assert.NotNull(vm.SpeedTest);
        Assert.NotNull(vm.NetworkRepair);
        Assert.NotNull(vm.Drivers);
        Assert.NotNull(vm.Logs);
        Assert.NotNull(vm.About);
    }

    [Fact]
    public void ElevationBadge_IsOneOfTwoValues()
    {
        var vm = new MainWindowViewModel();
        Assert.True(vm.ElevationBadge == "Administrator" || vm.ElevationBadge == "Standard user",
            $"Unexpected badge: {vm.ElevationBadge}");
    }

    [Fact]
    public void Title_NotEmpty()
    {
        var vm = new MainWindowViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.Title));
    }

    [Fact]
    public void Title_ReflectsElevation()
    {
        var vm = new MainWindowViewModel();
        if (vm.IsElevated)
            Assert.Contains("Admin", vm.Title);
        else
            Assert.Equal("SysManager", vm.Title);
    }

    [Fact]
    public void EachTabViewModel_HasCorrectType()
    {
        var vm = new MainWindowViewModel();
        Assert.IsType<DashboardViewModel>(vm.Dashboard);
        Assert.IsType<AppUpdatesViewModel>(vm.AppUpdates);
        Assert.IsType<WindowsUpdateViewModel>(vm.WindowsUpdate);
        Assert.IsType<SystemHealthViewModel>(vm.SystemHealth);
        Assert.IsType<CleanupViewModel>(vm.Cleanup);
        Assert.IsType<DeepCleanupViewModel>(vm.DeepCleanup);
        Assert.IsType<NetworkSharedState>(vm.NetworkShared);
        Assert.IsType<PingViewModel>(vm.Ping);
        Assert.IsType<TracerouteViewModel>(vm.Traceroute);
        Assert.IsType<SpeedTestViewModel>(vm.SpeedTest);
        Assert.IsType<NetworkRepairViewModel>(vm.NetworkRepair);
        Assert.IsType<DriversViewModel>(vm.Drivers);
        Assert.IsType<LogsViewModel>(vm.Logs);
        Assert.IsType<AboutViewModel>(vm.About);
    }

    [Fact]
    public void NavItems_ContainAll57()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(57, vm.NavItems.Count);
        var ids = vm.NavItems.Select(n => n.Id).ToList();

        // Dashboard
        Assert.Contains("nav-dashboard", ids);

        // System (10)
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

        // Gaming & Profiles (5)
        Assert.Contains("nav-gaming-profile", ids);
        Assert.Contains("nav-standby-cleaner", ids);
        Assert.Contains("nav-timer-resolution", ids);
        Assert.Contains("nav-cpu-affinity", ids);
        Assert.Contains("nav-display-profiles", ids);

        // Monitor (7)
        Assert.Contains("nav-processes", ids);
        Assert.Contains("nav-resource-history", ids);
        Assert.Contains("nav-privacy-monitor", ids);
        Assert.Contains("nav-app-alerts", ids);
        Assert.Contains("nav-file-lock", ids);
        Assert.Contains("nav-settings-watchdog", ids);
        Assert.Contains("nav-bandwidth-monitor", ids);

        // Cleanup (4)
        Assert.Contains("nav-cleanup", ids);
        Assert.Contains("nav-deep-cleanup", ids);
        Assert.Contains("nav-shortcut-cleaner", ids);
        Assert.Contains("nav-scheduled-maintenance", ids);

        // Storage (2)
        Assert.Contains("nav-disk-analyzer", ids);
        Assert.Contains("nav-duplicates", ids);

        // Network (5) — DNS changer + hosts editor merged into one DNS & Hosts tab
        Assert.Contains("nav-ping", ids);
        Assert.Contains("nav-traceroute", ids);
        Assert.Contains("nav-speed-test", ids);
        Assert.Contains("nav-network-repair", ids);
        Assert.Contains("nav-dns-hosts", ids);

        // Apps (3)
        Assert.Contains("nav-app-updates", ids);
        Assert.Contains("nav-bulk-installer", ids);
        Assert.Contains("nav-uninstaller", ids);

        // Privacy & Security (8)
        Assert.Contains("nav-privacy-settings", ids);
        Assert.Contains("nav-file-shredder", ids);
        Assert.Contains("nav-app-blocker", ids);
        Assert.Contains("nav-debloater", ids);
        Assert.Contains("nav-browser-cleaner", ids);
        Assert.Contains("nav-edge-onedrive", ids);
        Assert.Contains("nav-defender-tweaks", ids);
        Assert.Contains("nav-notification-blocker", ids);

        // Customization (3)
        Assert.Contains("nav-context-menu", ids);
        Assert.Contains("nav-dark-mode", ids);
        Assert.Contains("nav-volume-control", ids);

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
        vm.OpenAboutTabCommand.Execute(null);
        Assert.NotNull(vm.SelectedNav);
        Assert.Equal("nav-about", vm.SelectedNav!.Id);
    }

    [Fact]
    public void SelectedNav_DefaultsToDashboard()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.SelectedNav);
        Assert.Equal("nav-dashboard", vm.SelectedNav!.Id);
    }

    [Fact]
    public void NavItems_HaveUniqueIds()
    {
        var vm = new MainWindowViewModel();
        var ids = vm.NavItems.Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void NavItems_AllHaveLabelsAndGlyphs()
    {
        var vm = new MainWindowViewModel();
        Assert.All(vm.NavItems, n =>
        {
            Assert.False(string.IsNullOrWhiteSpace(n.Label));
            Assert.False(string.IsNullOrWhiteSpace(n.Glyph));
            Assert.NotNull(n.Content);
            Assert.NotNull(n.ViewType);
        });
    }

    // ── NavGroup tests ──────────────────────────────────────────────

    [Fact]
    public void NavGroups_Has12Groups()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(12, vm.NavGroups.Count);
    }

    [Fact]
    public void NavGroups_HaveUniqueIds()
    {
        var vm = new MainWindowViewModel();
        var ids = vm.NavGroups.Select(g => g.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void NavGroups_AllHaveChildren()
    {
        var vm = new MainWindowViewModel();
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
        var vm = new MainWindowViewModel();
        var singles = vm.NavGroups.Where(g => g.IsSingleItem).Select(g => g.Id).ToList();
        Assert.Contains("grp-dashboard", singles);
        Assert.Single(singles);
    }

    [Fact]
    public void NavGroups_SystemGroup_Contains10Items()
    {
        var vm = new MainWindowViewModel();
        var sys = vm.NavGroups.First(g => g.Id == "grp-system");
        Assert.Equal(10, sys.Children.Count);
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
        // Legacy Panels is a read-only applet launcher — it lives in Info, not System.
        Assert.DoesNotContain("nav-legacy-panels", ids);
    }

    [Fact]
    public void NavGroups_AppAlerts_LivesInMonitorNotPrivacy()
    {
        var vm = new MainWindowViewModel();
        var monitor = vm.NavGroups.First(g => g.Id == "grp-monitor").Children.Select(c => c.Id).ToList();
        var privacy = vm.NavGroups.First(g => g.Id == "grp-privacy").Children.Select(c => c.Id).ToList();
        // App Alerts passively watches for new installs — it belongs with the monitoring tabs.
        Assert.Contains("nav-app-alerts", monitor);
        Assert.DoesNotContain("nav-app-alerts", privacy);
    }

    [Fact]
    public void NavGroups_LegacyPanels_LivesInInfoNotSystem()
    {
        var vm = new MainWindowViewModel();
        var info = vm.NavGroups.First(g => g.Id == "grp-info").Children.Select(c => c.Id).ToList();
        var system = vm.NavGroups.First(g => g.Id == "grp-system").Children.Select(c => c.Id).ToList();
        Assert.Contains("nav-legacy-panels", info);
        Assert.DoesNotContain("nav-legacy-panels", system);
    }

    [Fact]
    public void NavGroups_StorageGroup_ContainsDiskAnalyzerAndDuplicates()
    {
        var vm = new MainWindowViewModel();
        var storage = vm.NavGroups.First(g => g.Id == "grp-storage");
        var ids = storage.Children.Select(c => c.Id).ToList();
        Assert.Contains("nav-disk-analyzer", ids);
        Assert.Contains("nav-duplicates", ids);
    }

    [Fact]
    public void NavGroups_CleanupGroup_Has4Items()
    {
        // Cleanup has: Quick, Deep, Shortcut Cleaner, Scheduled Maintenance.
        // File Shredder lives under Privacy & Security, not Cleanup.
        var vm = new MainWindowViewModel();
        var cleanup = vm.NavGroups.First(g => g.Id == "grp-cleanup");
        Assert.Equal(4, cleanup.Children.Count);
    }

    [Fact]
    public void NavGroups_FlatNavItems_MatchGroupChildren()
    {
        var vm = new MainWindowViewModel();
        var fromGroups = vm.NavGroups.SelectMany(g => g.Children).ToList();
        Assert.Equal(fromGroups.Count, vm.NavItems.Count);
        for (int i = 0; i < fromGroups.Count; i++)
            Assert.Same(fromGroups[i], vm.NavItems[i]);
    }

    [Fact]
    public void NavGroups_AllExpandedByDefault()
    {
        var vm = new MainWindowViewModel();
        Assert.All(vm.NavGroups, g => Assert.True(g.IsExpanded));
    }

    // ── Data-driven contract: every leaf in the live graph is well-formed ──
    // Enumerates the real nav tree instead of hard-coding ids, so a future
    // nav/wiring change that leaves a leaf without content or a view fails
    // automatically rather than being silently re-baselined.

    [Fact]
    public void NavLeaf_EveryItemHasContentAndResolvableView()
    {
        var vm = new MainWindowViewModel();
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
        var vm = new MainWindowViewModel();
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
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-timer-resolution");
        Assert.Equal(typeof(SysManager.Views.TimerResolutionView), item.ViewType);
        Assert.IsType<TimerResolutionViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_FileLock_IsImplementedAndGraduated()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-file-lock");
        Assert.Equal(typeof(SysManager.Views.FileLockView), item.ViewType);
        Assert.IsType<FileLockViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_DisplayProfiles_IsImplementedAndGraduated()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-display-profiles");
        Assert.Equal(typeof(SysManager.Views.DisplayProfileView), item.ViewType);
        Assert.IsType<DisplayProfileViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_CpuAffinity_IsImplementedAndGraduated()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-cpu-affinity");
        Assert.Equal(typeof(SysManager.Views.CpuAffinityView), item.ViewType);
        Assert.IsType<CpuAffinityViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_Defender_IsImplementedAndGraduated()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-defender-tweaks");
        Assert.Equal(typeof(SysManager.Views.DefenderView), item.ViewType);
        Assert.IsType<DefenderViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_TaskScheduler_IsImplementedAndGraduated()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-task-scheduler");
        Assert.Equal(typeof(SysManager.Views.TaskSchedulerView), item.ViewType);
        Assert.IsType<TaskSchedulerViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_DarkMode_IsImplementedAndGraduated()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-dark-mode");
        Assert.Equal(typeof(SysManager.Views.DarkModeView), item.ViewType);
        Assert.IsType<DarkModeViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavLeaf_StandbyCleaner_IsImplementedAndGraduated()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-standby-cleaner");
        Assert.Equal(typeof(SysManager.Views.StandbyMemoryView), item.ViewType);
        Assert.IsType<StandbyMemoryViewModel>(item.Content);
        Assert.False(item.IsInDevelopment);
    }

    // Resource History (#13) is implemented but newly added — it's wired to a real
    // view/VM and flagged PREVIEW (IsInDevelopment == true) until QA-verified, not a
    // PlaceholderView stub. This pins both facts so an accidental regression to the
    // placeholder, or a premature graduation, fails here.
    [Fact]
    public void NavLeaf_ResourceHistory_IsImplementedAndInPreview()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-resource-history");
        Assert.Equal(typeof(SysManager.Views.ResourceHistoryView), item.ViewType);
        Assert.IsType<ResourceHistoryViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }

    // Settings Watchdog (#335) — implemented, wired to a real view/VM, flagged PREVIEW.
    [Fact]
    public void NavLeaf_SettingsWatchdog_IsImplementedAndInPreview()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-settings-watchdog");
        Assert.Equal(typeof(SysManager.Views.SettingsWatchdogView), item.ViewType);
        Assert.IsType<SettingsWatchdogViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }

    // CLI Interface (#342) — implemented reference tab, wired to a real view/VM, flagged PREVIEW.
    [Fact]
    public void NavLeaf_CliInterface_IsImplementedAndInPreview()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-cli-interface");
        Assert.Equal(typeof(SysManager.Views.CliInterfaceView), item.ViewType);
        Assert.IsType<CliInterfaceViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }

    // Scheduled Maintenance (#10) — implemented, wired to a real view/VM, flagged PREVIEW.
    [Fact]
    public void NavLeaf_ScheduledMaintenance_IsImplementedAndInPreview()
    {
        var vm = new MainWindowViewModel();
        var item = vm.NavItems.First(n => n.Id == "nav-scheduled-maintenance");
        Assert.Equal(typeof(SysManager.Views.ScheduledMaintenanceView), item.ViewType);
        Assert.IsType<ScheduledMaintenanceViewModel>(item.Content);
        Assert.True(item.IsInDevelopment);
    }
}
