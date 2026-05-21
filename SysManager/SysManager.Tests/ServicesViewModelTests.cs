// SysManager · ServicesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ServicesViewModel"/> — filter logic, property defaults,
/// and command existence. Uses reflection to inject test data into the private
/// _allServices field to test ApplyFilter without hitting real WMI.
/// </summary>
public class ServicesViewModelTests
{
    private static readonly List<ServiceEntry> TestServices = new()
    {
        new() { Name = "wuauserv", DisplayName = "Windows Update", Description = "Manages Windows updates", Status = "Running", StartType = "Automatic", Recommendation = "keep-enabled" },
        new() { Name = "Spooler", DisplayName = "Print Spooler", Description = "Manages print jobs", Status = "Running", StartType = "Automatic", Recommendation = "safe-to-disable" },
        new() { Name = "XboxGipSvc", DisplayName = "Xbox Accessory Management", Description = "Manages Xbox accessories", Status = "Stopped", StartType = "Manual", Recommendation = "safe-to-disable" },
        new() { Name = "WSearch", DisplayName = "Windows Search", Description = "Provides content indexing", Status = "Running", StartType = "Automatic", Recommendation = "advanced" },
        new() { Name = "BITS", DisplayName = "Background Intelligent Transfer", Description = "Transfers files in background", Status = "Stopped", StartType = "Manual", Recommendation = "keep-enabled" },
    };

    private static ServicesViewModel CreateWithData(List<ServiceEntry>? services = null)
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        var field = typeof(ServicesViewModel).GetField("_allServices", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(vm, services ?? TestServices);

        // Trigger ApplyFilter so the Services collection reflects the injected data.
        var applyFilter = typeof(ServicesViewModel).GetMethod("ApplyFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        applyFilter.Invoke(vm, null);
        return vm;
    }

    // ── Constructor / Defaults ──

    [Fact]
    public void Constructor_Collections_NotNull()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.NotNull(vm.Services);
    }

    [Fact]
    public void Constructor_FilterOptions_ContainsExpected()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.Contains("All", vm.FilterOptions);
        Assert.Contains("Running", vm.FilterOptions);
        Assert.Contains("Stopped", vm.FilterOptions);
        Assert.Contains("Safe to disable", vm.FilterOptions);
        Assert.Contains("Advanced", vm.FilterOptions);
    }

    [Fact]
    public void Constructor_DefaultFilter_Empty()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.Equal("", vm.Filter);
    }

    [Fact]
    public void Constructor_DefaultSelectedFilter_All()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.Equal("All", vm.SelectedFilter);
    }

    [Fact]
    public void Constructor_Commands_Exist()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.StartServiceCommand);
        Assert.NotNull(vm.StopServiceCommand);
        Assert.NotNull(vm.DisableServiceCommand);
        Assert.NotNull(vm.EnableServiceCommand);
        Assert.NotNull(vm.ToggleHighlightCommand);
    }

    // ── ApplyFilter: category filters ──

    [Fact]
    public void ApplyFilter_All_ShowsAllServices()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "All";
        Assert.Equal(5, vm.Services.Count);
    }

    [Fact]
    public void ApplyFilter_Running_ShowsOnlyRunning()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "Running";
        Assert.All(vm.Services, s => Assert.Equal("Running", s.Status));
        Assert.Equal(3, vm.Services.Count);
    }

    [Fact]
    public void ApplyFilter_Stopped_ShowsOnlyStopped()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "Stopped";
        Assert.All(vm.Services, s => Assert.Equal("Stopped", s.Status));
        Assert.Equal(2, vm.Services.Count);
    }

    [Fact]
    public void ApplyFilter_SafeToDisable_ShowsOnlySafe()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "Safe to disable";
        Assert.All(vm.Services, s => Assert.Equal("safe-to-disable", s.Recommendation));
        Assert.Equal(2, vm.Services.Count);
    }

    [Fact]
    public void ApplyFilter_Advanced_ShowsOnlyAdvanced()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "Advanced";
        Assert.All(vm.Services, s => Assert.Equal("advanced", s.Recommendation));
        Assert.Single(vm.Services);
    }

    // ── ApplyFilter: text filter ──

    [Fact]
    public void ApplyFilter_TextFilter_MatchesDisplayName()
    {
        var vm = CreateWithData();
        vm.Filter = "Print";
        Assert.Single(vm.Services);
        Assert.Equal("Print Spooler", vm.Services[0].DisplayName);
    }

    [Fact]
    public void ApplyFilter_TextFilter_MatchesServiceName()
    {
        var vm = CreateWithData();
        vm.Filter = "wuauserv";
        Assert.Single(vm.Services);
        Assert.Equal("Windows Update", vm.Services[0].DisplayName);
    }

    [Fact]
    public void ApplyFilter_TextFilter_MatchesDescription()
    {
        var vm = CreateWithData();
        vm.Filter = "indexing";
        Assert.Single(vm.Services);
        Assert.Equal("Windows Search", vm.Services[0].DisplayName);
    }

    [Fact]
    public void ApplyFilter_TextFilter_CaseInsensitive()
    {
        var vm = CreateWithData();
        vm.Filter = "XBOX";
        Assert.Single(vm.Services);
        Assert.Equal("Xbox Accessory Management", vm.Services[0].DisplayName);
    }

    [Fact]
    public void ApplyFilter_TextFilter_NoMatch_ReturnsEmpty()
    {
        var vm = CreateWithData();
        vm.Filter = "zzz_nonexistent_zzz";
        Assert.Empty(vm.Services);
    }

    // ── ApplyFilter: combined text + category ──

    [Fact]
    public void ApplyFilter_TextAndCategory_Combined()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "Running";
        vm.Filter = "Update";
        Assert.Single(vm.Services);
        Assert.Equal("Windows Update", vm.Services[0].DisplayName);
    }

    [Fact]
    public void ApplyFilter_TextAndCategory_NoOverlap_Empty()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "Stopped";
        vm.Filter = "Windows Update";
        Assert.Empty(vm.Services);
    }

    // ── ApplyFilter: sorting ──

    [Fact]
    public void ApplyFilter_SortsByDisplayName()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "All";
        var names = vm.Services.Select(s => s.DisplayName).ToList();
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, names);
    }

    // ── ApplyFilter: empty data ──

    [Fact]
    public void ApplyFilter_EmptyList_NoException()
    {
        var vm = CreateWithData(new List<ServiceEntry>());
        vm.SelectedFilter = "Running";
        Assert.Empty(vm.Services);
    }

    // ── Property change triggers filter ──

    [Fact]
    public void SelectedFilter_Change_TriggersRefilter()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "All";
        Assert.Equal(5, vm.Services.Count);
        vm.SelectedFilter = "Stopped";
        Assert.Equal(2, vm.Services.Count);
    }

    [Fact]
    public void Filter_Change_TriggersRefilter()
    {
        var vm = CreateWithData();
        vm.SelectedFilter = "All";
        Assert.Equal(5, vm.Services.Count);
        vm.Filter = "Xbox";
        Assert.Single(vm.Services);
    }
}
