// SysManager · PerformanceViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PerformanceViewModel"/>. Verifies initial state,
/// per-section commands, and property defaults.
/// </summary>
public class PerformanceViewModelTests
{
    private static PerformanceViewModel CreateVm() => new(new PowerShellRunner());

    // ── Commands exist ──

    [Fact]
    public void Constructor_GlobalCommands_Exist()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.RestoreAllCommand);
    }

    [Fact]
    public void Constructor_PerSectionCommands_Exist()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.ApplyPowerPlanCommand);
        Assert.NotNull(vm.ApplyVisualEffectsCommand);
        Assert.NotNull(vm.ApplyGameModeCommand);
        Assert.NotNull(vm.ApplyXboxGameBarCommand);
        Assert.NotNull(vm.ApplyGpuCommand);
        Assert.NotNull(vm.ApplyProcessorStateCommand);
    }

    // ── Default state ──

    [Fact]
    public void Constructor_Profile_NotNull()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.Profile);
    }

    [Fact]
    public void Constructor_Summary_HasDefaultValue()
    {
        var vm = CreateVm();
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    [Fact]
    public void Constructor_SelectedPlan_DefaultBalanced()
    {
        var vm = CreateVm();
        Assert.Equal("balanced", vm.SelectedPlan);
    }

    [Fact]
    public void Constructor_HasSnapshot_DefaultFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.HasSnapshot);
    }

    [Fact]
    public void Constructor_NeedsReboot_DefaultFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.NeedsReboot);
    }

    [Fact]
    public void Constructor_WantToggles_DefaultFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.WantVisualEffectsReduced);
        Assert.False(vm.WantGameModeOff);
        Assert.False(vm.WantXboxGameBarOff);
        Assert.False(vm.WantGpuMaxPerformance);
        Assert.False(vm.WantProcessorMaxState);
    }

    // ── Property changes ──

    [Fact]
    public void SelectedPlan_CanBeChanged()
    {
        var vm = CreateVm();
        vm.SelectedPlan = "ultimate";
        Assert.Equal("ultimate", vm.SelectedPlan);
    }

    [Fact]
    public void WantVisualEffectsReduced_CanBeToggled()
    {
        var vm = CreateVm();
        vm.WantVisualEffectsReduced = true;
        Assert.True(vm.WantVisualEffectsReduced);
        vm.WantVisualEffectsReduced = false;
        Assert.False(vm.WantVisualEffectsReduced);
    }

    [Fact]
    public void WantGameModeOff_CanBeToggled()
    {
        var vm = CreateVm();
        vm.WantGameModeOff = true;
        Assert.True(vm.WantGameModeOff);
    }

    [Fact]
    public void WantXboxGameBarOff_CanBeToggled()
    {
        var vm = CreateVm();
        vm.WantXboxGameBarOff = true;
        Assert.True(vm.WantXboxGameBarOff);
    }

    [Fact]
    public void WantGpuMaxPerformance_CanBeToggled()
    {
        var vm = CreateVm();
        vm.WantGpuMaxPerformance = true;
        Assert.True(vm.WantGpuMaxPerformance);
    }

    [Fact]
    public void WantProcessorMaxState_CanBeToggled()
    {
        var vm = CreateVm();
        vm.WantProcessorMaxState = true;
        Assert.True(vm.WantProcessorMaxState);
    }

    [Fact]
    public void NvidiaGpuName_DefaultEmpty()
    {
        var vm = CreateVm();
        Assert.Equal("", vm.NvidiaGpuName);
    }

    [Fact]
    public void HasNvidiaGpu_DefaultFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.HasNvidiaGpu);
    }

    [Fact]
    public void SelectedPlan_NotifiesPropertyChanged()
    {
        var vm = CreateVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.SelectedPlan = "high";
        Assert.Contains("SelectedPlan", changed);
    }
}
