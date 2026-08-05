// SysManager · DisplayProfileViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.Input;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DisplayProfileViewModel"/> — pins that the display-mode
/// commands are asynchronous, so their blocking P/Invoke work (EnumDisplaySettings /
/// ChangeDisplaySettingsEx) runs off the UI thread and the auto-revert DispatcherTimer
/// keeps ticking during a mode switch.
/// </summary>
[Collection("DialogService")]
public class DisplayProfileViewModelTests
{
    private static DisplayProfileViewModel NewVm()
    {
        var vm = new DisplayProfileViewModel(new DisplayProfileService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void ApplyCommand_IsAsync()
    {
        // An IAsyncRelayCommand means the body is awaited (offloaded), not run
        // synchronously on the dispatcher — and it disables itself while in flight.
        var vm = NewVm();
        Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.ApplyCommand);
    }

    [Fact]
    public void RevertNowCommand_IsAsync()
    {
        var vm = NewVm();
        Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RevertNowCommand);
    }

    [Fact]
    public void NewVm_DoesNotThrow_AndExposesCollections()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Displays);
        Assert.NotNull(vm.Modes);
    }

    [Fact]
    public void Revert_CapturesTheAppliedDevice_NotJustTheMode()
    {
        // Regression pin for the cross-display revert bug: the auto-revert must target
        // the display the change was APPLIED to (captured at Apply time), not whatever
        // is selected when the 15 s timer fires. That requires the VM to remember the
        // device alongside the previous mode — assert the capture field exists.
        var device = typeof(DisplayProfileViewModel).GetField(
            "_previousDevice", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(device);
        Assert.Equal("SysManager.Models.DisplayDevice", device!.FieldType.FullName);
    }

    // ── Progress feedback (regression) ──
    // DisplayProfileView.xaml binds a progress bar to IsBusy, and the sidebar spinner reads the same
    // flag, but this VM never assigned it — so applying a mode (which can block for seconds while the
    // driver re-trains the panel) gave no feedback at all. DisplayProfileService is sealed with no
    // interface, so these assert the observable end state rather than substituting the P/Invoke.

    [Fact]
    public async Task AfterInitAndItsChainedModeLoad_TheBusyFlagIsClear()
    {
        // Covers both branches of LoadDisplaysAsync — displays found and none found — because either
        // way the finally must release the flag. Left set, the bar would spin forever on tab open.
        //
        // Init CHAINS: LoadDisplaysAsync assigns SelectedDisplay, whose handler calls InitializeAsync
        // again — which REPLACES InitializationComplete with the mode-load task. So awaiting the
        // handle once returns while the mode load is still running with the flag legitimately raised.
        // Re-reading the property after the first await yields that second task; awaiting it too is
        // what makes this deterministic instead of a race. (With no displays, the early return means
        // no second task was ever created and the handle is simply already complete.)
        var vm = NewVm();                        // NewVm already awaited the first task
        await vm.InitializationComplete;         // re-read: the chained mode load, if one started

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task LoadingModesForAnUnknownDevice_ClearsTheBusyFlag()
    {
        // A device name no adapter answers to: EnumDisplaySettings returns nothing, the last-writer
        // guard bails out early via `return` — and the finally still has to run.
        var vm = NewVm();
        var load = typeof(DisplayProfileViewModel).GetMethod(
            "LoadModesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await (Task)load.Invoke(vm, [@"\\.\DISPLAY_DOES_NOT_EXIST"])!;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task AnOlderModeLoadFinishingDoesNotClearANewerOnesBusyFlag()
    {
        // Rapid display switches launch overlapping loads (the VM documents this). If every completion
        // cleared the flag, the older one finishing would hide the bar while the newer load was still
        // working — so only the newest generation may release it.
        var vm = NewVm();
        var load = typeof(DisplayProfileViewModel).GetMethod(
            "LoadModesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var generation = typeof(DisplayProfileViewModel).GetField(
            "_modeLoadGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Start the older load. Invoking runs synchronously up to its first await, so by now it has
        // claimed a generation and raised the flag.
        var older = (Task)load.Invoke(vm, [@"\\.\DISPLAY_A"])!;
        int olderGeneration = (int)generation.GetValue(vm)!;

        // Model a newer load starting while that one is still in flight.
        generation.SetValue(vm, olderGeneration + 1);
        vm.IsBusy = true;
        vm.IsProgressIndeterminate = true;

        await older;

        // The older completion saw a newer generation and left the flag alone.
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsProgressIndeterminate);
    }
}
