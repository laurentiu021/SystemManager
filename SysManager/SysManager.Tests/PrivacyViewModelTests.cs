// SysManager · PrivacyViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PrivacyViewModel"/>. Verifies toggle population,
/// category filtering, and reset behavior without writing to the registry.
/// </summary>
public class PrivacyViewModelTests
{
    private static PrivacyViewModel CreateVm() => new(new PrivacyService());

    [Fact]
    public void Constructor_Toggles_Populated_With12Items()
    {
        var vm = CreateVm();
        Assert.Equal(12, vm.Toggles.Count);
    }

    [Fact]
    public void Constructor_Categories_ContainsAllPlusSpecific()
    {
        var vm = CreateVm();
        Assert.Contains("All", vm.Categories);
        Assert.Contains("Telemetry", vm.Categories);
        Assert.Contains("UI Declutter", vm.Categories);
        Assert.Contains("Features", vm.Categories);
        Assert.Equal(4, vm.Categories.Count);
    }

    [Fact]
    public void Constructor_SelectedCategory_DefaultsToAll()
    {
        var vm = CreateVm();
        Assert.Equal("All", vm.SelectedCategory);
    }

    [Theory]
    [InlineData("Telemetry", 4)]
    [InlineData("UI Declutter", 4)]
    [InlineData("Features", 4)]
    public void FilterByCategory_ShowsOnlyMatchingToggles(string category, int expectedCount)
    {
        var vm = CreateVm();
        vm.SelectedCategory = category;
        Assert.Equal(expectedCount, vm.FilteredToggles.Count);
        Assert.All(vm.FilteredToggles, t => Assert.Equal(category, t.Category));
    }

    [Fact]
    public void FilterByAll_ShowsAllToggles()
    {
        var vm = CreateVm();
        vm.SelectedCategory = "Telemetry"; // Filter first
        vm.SelectedCategory = "All";       // Then reset
        Assert.Equal(12, vm.FilteredToggles.Count);
    }

    [Fact]
    public void ResetAll_SetsAllIsEnabledToFalse()
    {
        var vm = CreateVm();
        // Some toggles may be enabled from registry reads — force some on
        foreach (var toggle in vm.Toggles)
            toggle.IsEnabled = true;

        vm.ResetAllCommand.Execute(null);

        Assert.All(vm.Toggles, t => Assert.False(t.IsEnabled));
    }

    [Fact]
    public void FilteredToggles_InitiallyMatchesAll()
    {
        var vm = CreateVm();
        Assert.Equal(vm.Toggles.Count, vm.FilteredToggles.Count);
    }

    [Fact]
    public void StatusMessage_UpdatesAfterReset()
    {
        var vm = CreateVm();
        vm.ResetAllCommand.Execute(null);
        Assert.Contains("reset", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
