// SysManager · BatteryHealthViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BatteryHealthViewModel"/>. Verifies initial state
/// and command availability.
/// </summary>
public class BatteryHealthViewModelTests
{
    [Fact]
    public void Constructor_RefreshCommand_Exists()
    {
        var vm = new BatteryHealthViewModel(new Services.BatteryService());
        Assert.NotNull(vm.RefreshCommand);
    }

    [Fact]
    public void Constructor_Battery_NotNull()
    {
        var vm = new BatteryHealthViewModel(new Services.BatteryService());
        Assert.NotNull(vm.Battery);
    }

    [Fact]
    public void Summary_HasDefaultValue()
    {
        var vm = new BatteryHealthViewModel(new Services.BatteryService());
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    [Fact]
    public void Battery_CanBeReplaced()
    {
        var vm = new BatteryHealthViewModel(new Services.BatteryService());
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Battery = new Models.BatteryInfo { Name = "Test" };
        Assert.Contains("Battery", changed);
        Assert.Equal("Test", vm.Battery.Name);
    }

    [Fact]
    public void Summary_CanBeChanged()
    {
        // Asserts the NOTIFICATION, not just the round-trip. A set/get pair passes on a plain auto-
        // property, so it would stay green if [ObservableProperty] were dropped from Summary — and a
        // bound label silently freezing is the only regression this property realistically has.
        // Battery_CanBeReplaced two tests above already uses this pattern.
        var vm = new BatteryHealthViewModel(new Services.BatteryService());
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Summary = "Custom summary";

        Assert.Contains("Summary", changed);
        Assert.Equal("Custom summary", vm.Summary);
    }
}
