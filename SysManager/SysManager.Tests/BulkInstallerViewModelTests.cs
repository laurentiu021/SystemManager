// SysManager · BulkInstallerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BulkInstallerViewModel"/>. Verifies curated app list,
/// filtering, selection commands, and category logic.
/// </summary>
public class BulkInstallerViewModelTests
{
    private static BulkInstallerViewModel CreateVm() =>
        new(new BulkInstallerService(new PowerShellRunner()));

    [Fact]
    public void Constructor_PopulatesAppsWithCuratedList()
    {
        var vm = CreateVm();
        Assert.Equal(46, vm.Apps.Count);
    }

    [Fact]
    public void Constructor_FilteredAppsMatchesAllApps()
    {
        var vm = CreateVm();
        Assert.Equal(vm.Apps.Count, vm.FilteredApps.Count);
    }

    [Fact]
    public void SelectAll_SelectsAllFilteredApps()
    {
        var vm = CreateVm();
        vm.SelectAllCommand.Execute(null);
        Assert.All(vm.FilteredApps, app => Assert.True(app.IsSelected));
    }

    [Fact]
    public void DeselectAll_DeselectsAllApps()
    {
        var vm = CreateVm();
        // First select all, then deselect
        vm.SelectAllCommand.Execute(null);
        vm.DeselectAllCommand.Execute(null);
        Assert.All(vm.Apps, app => Assert.False(app.IsSelected));
    }

    [Theory]
    [InlineData("Browsers", 4)]
    [InlineData("Communication", 6)]
    [InlineData("Media", 3)]
    [InlineData("Development", 4)]
    [InlineData("Utilities", 8)]
    [InlineData("Gaming", 3)]
    [InlineData("Security", 2)]
    [InlineData("Office & Productivity", 4)]
    [InlineData("Creativity", 4)]
    [InlineData("Networking & VPN", 4)]
    [InlineData("Runtimes & Frameworks", 4)]
    public void FilterByCategory_ShowsOnlyMatchingCategory(string category, int expectedCount)
    {
        var vm = CreateVm();
        vm.SelectedCategory = category;
        Assert.Equal(expectedCount, vm.FilteredApps.Count);
        Assert.All(vm.FilteredApps, app => Assert.Equal(category, app.Category));
    }

    [Theory]
    [InlineData("Chrome", 1)]
    [InlineData("fire", 1)]
    [InlineData("zzz_nonexistent", 0)]
    public void FilterByText_ShowsMatchingName(string text, int expectedCount)
    {
        var vm = CreateVm();
        vm.FilterText = text;
        Assert.Equal(expectedCount, vm.FilteredApps.Count);
    }

    [Fact]
    public void CombinedFilter_CategoryAndText_Works()
    {
        var vm = CreateVm();
        vm.SelectedCategory = "Development";
        vm.FilterText = "Git";
        Assert.Single(vm.FilteredApps);
        Assert.Equal("Git", vm.FilteredApps[0].Name);
    }

    [Fact]
    public void Categories_ContainsAllAndElevenSpecificPlusCustom()
    {
        var vm = CreateVm();
        Assert.Contains("All", vm.Categories);
        Assert.Contains("Custom", vm.Categories);
        Assert.Equal(13, vm.Categories.Count);
    }
}
