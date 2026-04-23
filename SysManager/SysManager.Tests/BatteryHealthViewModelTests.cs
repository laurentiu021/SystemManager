// SysManager · BatteryHealthViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
        var vm = new BatteryHealthViewModel();
        Assert.NotNull(vm.RefreshCommand);
    }

    [Fact]
    public void Constructor_Battery_NotNull()
    {
        var vm = new BatteryHealthViewModel();
        Assert.NotNull(vm.Battery);
    }

    [Fact]
    public void Summary_HasDefaultValue()
    {
        var vm = new BatteryHealthViewModel();
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    [Fact]
    public void Battery_CanBeReplaced()
    {
        var vm = new BatteryHealthViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Battery = new Models.BatteryInfo { Name = "Test" };
        Assert.Contains("Battery", changed);
        Assert.Equal("Test", vm.Battery.Name);
    }

    [Fact]
    public void Summary_CanBeChanged()
    {
        var vm = new BatteryHealthViewModel();
        vm.Summary = "Custom summary";
        Assert.Equal("Custom summary", vm.Summary);
    }
}
