// SysManager · MainWindowViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
        Assert.NotNull(vm.Network);
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
        Assert.IsType<NetworkViewModel>(vm.Network);
        Assert.IsType<DriversViewModel>(vm.Drivers);
        Assert.IsType<LogsViewModel>(vm.Logs);
        Assert.IsType<AboutViewModel>(vm.About);
    }

    [Fact]
    public void NavItems_ContainAll18()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(18, vm.NavItems.Count);
        var ids = vm.NavItems.Select(n => n.Id).ToList();
        Assert.Contains("nav-dashboard", ids);
        Assert.Contains("nav-system-health", ids);
        Assert.Contains("nav-performance", ids);
        Assert.Contains("nav-services", ids);
        Assert.Contains("nav-startup", ids);
        Assert.Contains("nav-processes", ids);
        Assert.Contains("nav-cleanup", ids);
        Assert.Contains("nav-deep-cleanup", ids);
        Assert.Contains("nav-disk-analyzer", ids);
        Assert.Contains("nav-duplicates", ids);
        Assert.Contains("nav-network", ids);
        Assert.Contains("nav-app-updates", ids);
        Assert.Contains("nav-windows-update", ids);
        Assert.Contains("nav-uninstaller", ids);
        Assert.Contains("nav-drivers", ids);
        Assert.Contains("nav-battery", ids);
        Assert.Contains("nav-logs", ids);
        Assert.Contains("nav-about", ids);
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
    public void NavGroups_Has7Groups()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(7, vm.NavGroups.Count);
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
    public void NavGroups_SingleItemGroups_AreDashboardAndNetwork()
    {
        var vm = new MainWindowViewModel();
        var singles = vm.NavGroups.Where(g => g.IsSingleItem).Select(g => g.Id).ToList();
        Assert.Contains("grp-dashboard", singles);
        Assert.Contains("grp-network", singles);
        Assert.Equal(2, singles.Count);
    }

    [Fact]
    public void NavGroups_SystemGroup_Contains5Items()
    {
        var vm = new MainWindowViewModel();
        var sys = vm.NavGroups.First(g => g.Id == "grp-system");
        Assert.Equal(5, sys.Children.Count);
        var ids = sys.Children.Select(c => c.Id).ToList();
        Assert.Contains("nav-system-health", ids);
        Assert.Contains("nav-performance", ids);
        Assert.Contains("nav-services", ids);
        Assert.Contains("nav-startup", ids);
        Assert.Contains("nav-processes", ids);
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
    public void NavGroups_CleanupGroup_DoesNotContainLargeFiles()
    {
        // #98: Large File Finder moved from Deep Cleanup to Storage
        var vm = new MainWindowViewModel();
        var cleanup = vm.NavGroups.First(g => g.Id == "grp-cleanup");
        Assert.Equal(2, cleanup.Children.Count);
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
}
