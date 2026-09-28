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
        var changed = vm.RecordPropertyChanges();

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
        var changed = vm.RecordPropertyChanges();

        vm.Summary = "Custom summary";

        Assert.Contains("Summary", changed);
        Assert.Equal("Custom summary", vm.Summary);
    }

    // ── A read that failed is not "no battery" (#2503) ──
    //
    // The service reported a failed read as no battery, so a laptop was told "No battery detected — this device
    // runs on AC power only." The view model's own "Could not read battery information." was never reached.

    private static readonly Models.BatteryInfo Laptop = new()
    {
        HasBattery = true,
        Name = "Primary",
        ChargePercent = 80,
        Status = "Charging",
    };

    private static async Task<BatteryHealthViewModel> SettledVm(Func<Task<Models.BatteryInfo?>> read)
    {
        var vm = new BatteryHealthViewModel(read);
        await vm.InitializationComplete;
        return vm;
    }

    [Fact]
    public async Task AReadThatFailed_SaysSo_NotThatThereIsNoBattery()
    {
        var vm = await SettledVm(() => Task.FromResult<Models.BatteryInfo?>(null));

        Assert.True(vm.ReadFailed);
        Assert.Equal("Could not read the battery. Press Refresh to try again.", vm.Summary);
        Assert.Equal("The battery could not be read.", vm.StatusMessage);
        Assert.StartsWith("The battery could not be read", vm.NoBatteryText);
        Assert.DoesNotContain("AC power", vm.Summary);
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheLastReading_AndSaysItIsFromThen()
    {
        var reads = 0;
        var vm = await SettledVm(() => Task.FromResult(++reads == 1 ? Laptop : null));
        var summary = vm.Summary;
        Assert.True(vm.Battery.HasBattery);   // the premise: the first read showed the battery

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Same(Laptop, vm.Battery);
        Assert.Equal(summary, vm.Summary);
        Assert.Contains("from the last reading", vm.StatusMessage);
    }

    [Fact]
    public async Task NoBattery_IsStillReportedAsNoBattery()
    {
        var vm = await SettledVm(() => Task.FromResult<Models.BatteryInfo?>(new Models.BatteryInfo()));

        Assert.False(vm.ReadFailed);
        Assert.Equal("No battery detected on this device.", vm.NoBatteryText);
        Assert.Equal("No battery found.", vm.StatusMessage);
    }

    [Fact]
    public async Task AReadThatWorksAfterAFailedOne_ClearsTheFailure()
    {
        var reads = 0;
        var vm = await SettledVm(() => Task.FromResult(++reads == 1 ? null : Laptop));
        Assert.True(vm.ReadFailed);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.ReadFailed);
        Assert.Same(Laptop, vm.Battery);
        Assert.Equal("Battery data loaded.", vm.StatusMessage);
    }
}
