// SysManager · PerformanceViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PerformanceViewModel"/>. Verifies initial state,
/// per-section commands, and property defaults.
/// </summary>
/// <remarks>
/// In the DialogService collection (serialized): the operation-lock regression
/// tests below swap the global <see cref="DialogService.Instance"/> and take the
/// shared <see cref="OperationLockService"/>, both process-wide singletons.
/// </remarks>
[Collection("DialogService")]
public class PerformanceViewModelTests
{
    private static PerformanceViewModel NewVm()
    {
        var ps = new PowerShellRunner();
        return new(new PerformanceService(ps, new RestorePointService(ps)));
    }

    // ── Commands exist ──

    [Fact]
    public void Constructor_GlobalCommands_Exist()
    {
        var vm = NewVm();
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.RestoreAllCommand);
    }

    [Fact]
    public void Constructor_PerSectionCommands_Exist()
    {
        var vm = NewVm();
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
        var vm = NewVm();
        Assert.NotNull(vm.Profile);
    }

    [Fact]
    public void Constructor_Summary_HasDefaultValue()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    [Fact]
    public void Constructor_SelectedPlan_DefaultBalanced()
    {
        var vm = NewVm();
        Assert.Equal("balanced", vm.SelectedPlan);
    }

    [Fact]
    public void Constructor_HasSnapshot_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.HasSnapshot);
    }

    [Fact]
    public void Constructor_NeedsReboot_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.NeedsReboot);
    }

    [Fact]
    public void Constructor_WantToggles_DefaultFalse()
    {
        var vm = NewVm();
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
        var vm = NewVm();
        vm.SelectedPlan = "ultimate";
        Assert.Equal("ultimate", vm.SelectedPlan);
    }

    [Fact]
    public void WantVisualEffectsReduced_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantVisualEffectsReduced = true;
        Assert.True(vm.WantVisualEffectsReduced);
        vm.WantVisualEffectsReduced = false;
        Assert.False(vm.WantVisualEffectsReduced);
    }

    [Fact]
    public void WantGameModeOff_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantGameModeOff = true;
        Assert.True(vm.WantGameModeOff);
    }

    [Fact]
    public void WantXboxGameBarOff_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantXboxGameBarOff = true;
        Assert.True(vm.WantXboxGameBarOff);
    }

    [Fact]
    public void WantGpuMaxPerformance_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantGpuMaxPerformance = true;
        Assert.True(vm.WantGpuMaxPerformance);
    }

    [Fact]
    public void WantProcessorMaxState_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantProcessorMaxState = true;
        Assert.True(vm.WantProcessorMaxState);
    }

    [Fact]
    public void NvidiaGpuName_DefaultEmpty()
    {
        var vm = NewVm();
        Assert.Equal("", vm.NvidiaGpuName);
    }

    [Fact]
    public void HasNvidiaGpu_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.HasNvidiaGpu);
    }

    [Fact]
    public void SelectedPlan_NotifiesPropertyChanged()
    {
        var vm = NewVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.SelectedPlan = "high";
        Assert.Contains("SelectedPlan", changed);
    }

    // ── Processor state lock (issue #103) ──

    [Fact]
    public void IsProcessorStateLocked_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsProcessorStateLocked);
    }

    [Fact]
    public void IsProcessorStateEditable_InverseOfLocked()
    {
        var vm = NewVm();
        Assert.True(vm.IsProcessorStateEditable);
        vm.IsProcessorStateLocked = true;
        Assert.False(vm.IsProcessorStateEditable);
    }

    [Fact]
    public void IsProcessorStateLocked_NotifiesEditable()
    {
        var vm = NewVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.IsProcessorStateLocked = true;
        Assert.Contains("IsProcessorStateLocked", changed);
        Assert.Contains("IsProcessorStateEditable", changed);
    }

    [Fact]
    public void TrimRamCommand_IsAsync()
    {
        // TrimRam enumerates every process and calls EmptyWorkingSet (P/Invoke) on each;
        // it must run off the UI thread. An IAsyncRelayCommand proves the work is awaited
        // (offloaded), not executed synchronously on the dispatcher.
        var vm = NewVm();
        Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(vm.TrimRamCommand);
    }

    // ── System-modification lock (ultra-audit #46) ──
    //
    // Every mutating command (Apply* / Restore All / Trim RAM / Create restore point /
    // Toggle hibernation) must serialize through OperationLockService before touching the
    // system. Without it, Restore All can null the snapshot mid-Apply and leave a tweak
    // applied with nothing to revert it. These pin the guard the way ShortcutCleaner's
    // DeleteSelected_WhenDiskLocked_DoesNotDelete pins its Disk-lock guard: stub the dialog
    // to "Yes", hold the SystemModification lock, and prove the command bails at the guard.

    private static void SeedSnapshot(PerformanceViewModel vm)
    {
        // Restore All early-returns when _snapshot is null (before the lock guard). Seed a
        // snapshot via the private field so the command reaches the guard we're testing.
        var snapshot = new PerformanceService.OriginalSnapshot(
            PowerPlanGuid: "guid", PowerPlanName: "Balanced", UiEffectsEnabled: true,
            GameModeEnabled: true, XboxGameBarEnabled: true, XboxGameDvrEnabled: true,
            GpuDynamicPstate: true, ProcessorMinPercentAc: 5, NvidiaSubKey: null);
        typeof(PerformanceViewModel)
            .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, snapshot);
    }

    [Fact]
    public async Task RestoreAll_WhenSystemModificationLocked_BailsAtGuard()
    {
        var vm = NewVm();
        SeedSnapshot(vm);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;

        // Hold the SystemModification lock so Restore All must bail rather than race an Apply.
        using var held = OperationLockService.Instance.TryAcquire(
            OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);
        try
        {
            await vm.RestoreAllCommand.ExecuteAsync(null);

            // Confirm was shown, but the lock was unavailable → the command reported the
            // contention and did NOT run the restore body (which nulls _snapshot). The seeded
            // snapshot survives, proving the guard short-circuited before the mutation.
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Contains("already running", vm.StatusMessage);
            var snapshotAfter = typeof(PerformanceViewModel)
                .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm);
            Assert.NotNull(snapshotAfter);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public async Task TrimRam_WhenSystemModificationLocked_BailsAtGuard()
    {
        var vm = NewVm();

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;

        using var held = OperationLockService.Instance.TryAcquire(
            OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);
        try
        {
            await vm.TrimRamCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Contains("already running", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public async Task ApplyPowerPlan_WhenSystemModificationLocked_BailsAtGuard()
    {
        var vm = NewVm();
        // Force SelectedPlan away from the current plan so the "already set" early-return
        // (before the lock guard) doesn't short-circuit the command first.
        vm.SelectedPlan = "ultimate";

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;

        using var held = OperationLockService.Instance.TryAcquire(
            OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);
        try
        {
            await vm.ApplyPowerPlanCommand.ExecuteAsync(null);
            Assert.Contains("already running", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }
}
