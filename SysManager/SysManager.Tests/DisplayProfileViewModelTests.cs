// SysManager · DisplayProfileViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.Input;
using NSubstitute;
using SysManager.Models;
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
[Collection("ProcessWideStatics")]
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

    // ---------- reading the displays again (#2886) ----------
    // The displays were read once, when the tab was first built, and the view model lives as long as SysManager, which
    // keeps running in the notification area. A screen connected later never appeared until SysManager restarted.

    private static readonly DisplayDevice Laptop = new(@"\\.\DISPLAY1", "Built-in display", IsPrimary: true, IsActive: true);
    private static readonly DisplayDevice Monitor = new(@"\\.\DISPLAY2", "Gaming monitor", IsPrimary: false, IsActive: true);
    private static readonly DisplayMode At60 = new(1920, 1080, 60, 32);
    private static readonly DisplayMode At144 = new(1920, 1080, 144, 32);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A substitute listing <paramref name="first"/> at construction and <paramref name="after"/> on every read after it.
    /// Every display offers 144 and 60 Hz, and runs at 60.
    /// </summary>
    private static IDisplayProfileService Listing(IReadOnlyList<DisplayDevice> first, IReadOnlyList<DisplayDevice> after)
    {
        var service = Substitute.For<IDisplayProfileService>();
        service.GetDisplays().Returns(first, after);
        service.GetSupportedModes(Arg.Any<string>()).Returns([At144, At60]);
        service.GetCurrentMode(Arg.Any<string>()).Returns(At60);
        return service;
    }

    /// <summary>
    /// Waits for a read of the displays and the mode load it starts. Each runs through <c>InitializeAsync</c>, which
    /// replaces <c>InitializationComplete</c>, so the property is read again after the first wait.
    /// </summary>
    private static async Task Settled(DisplayProfileViewModel vm)
    {
        await vm.InitializationComplete;
        await vm.InitializationComplete;
    }

    private static async Task<DisplayProfileViewModel> Loaded(IDisplayProfileService service)
    {
        var vm = new DisplayProfileViewModel(service);
        await Settled(vm);
        return vm;
    }

    [Fact]
    public async Task F5_ReadsTheDisplaysAgain()
    {
        var vm = await Loaded(Listing([Laptop], [Laptop]));

        Assert.Same(vm.RefreshDisplaysCommand, vm.RefreshOnF5);
    }

    [Fact]
    public async Task Refresh_ListsAScreenConnectedSinceTheTabWasBuilt()
    {
        var vm = await Loaded(Listing([Laptop], [Laptop, Monitor]));
        Assert.Equal([Laptop], vm.Displays);

        await vm.RefreshDisplaysCommand.ExecuteAsync(null);
        await Settled(vm);

        Assert.Equal([Laptop, Monitor], vm.Displays);
        Assert.Equal(Laptop, vm.SelectedDisplay);
        Assert.Equal("Pick a resolution and refresh rate, then apply.", vm.StatusMessage);
    }

    [Fact]
    public async Task Refresh_KeepsTheSelectedDisplay_AndReadsItsModesAgain()
    {
        var service = Listing([Laptop, Monitor], [Monitor, Laptop]);
        var vm = await Loaded(service);
        vm.SelectedDisplay = Monitor;
        await Settled(vm);
        // Set to 144 Hz in Windows' settings meanwhile. The same display is selected again, so only reading its modes
        // again shows it.
        service.GetCurrentMode(Monitor.DeviceName).Returns(At144);

        await vm.RefreshDisplaysCommand.ExecuteAsync(null);
        await Settled(vm);

        Assert.Equal(Monitor, vm.SelectedDisplay);
        Assert.Equal(At144, vm.CurrentMode);
        Assert.Equal(At144, vm.SelectedMode);
    }

    [Fact]
    public async Task Refresh_AfterTheSelectedScreenWasRemoved_SelectsThePrimary()
    {
        var vm = await Loaded(Listing([Laptop, Monitor], [Laptop]));
        vm.SelectedDisplay = Monitor;
        await Settled(vm);

        await vm.RefreshDisplaysCommand.ExecuteAsync(null);
        await Settled(vm);

        Assert.Equal([Laptop], vm.Displays);
        Assert.Equal(Laptop, vm.SelectedDisplay);
    }

    [Fact]
    public async Task AWindowsDisplayChange_ReadsTheDisplaysAgain_AndLeavesTheStatusLineAlone()
    {
        // The change may be SysManager's own, and the status line can hold what an apply or a revert said.
        var service = Listing([Laptop], [Laptop, Monitor]);
        var vm = await Loaded(service);
        vm.KeepSettingsCommand.Execute(null);
        Assert.Equal("Display set to 1920 × 1080 @ 60 Hz.", vm.StatusMessage);   // the premise: a line of the tab's own

        service.DisplaysChanged += Raise.Event();
        await Settled(vm);

        Assert.Equal([Laptop, Monitor], vm.Displays);
        Assert.Equal("Display set to 1920 × 1080 @ 60 Hz.", vm.StatusMessage);
    }

    [Fact]
    public async Task AWindowsDisplayChange_WhileAChangeWaitsToBeKept_IsReadOnceItIsKept()
    {
        var service = Listing([Laptop], [Laptop, Monitor]);
        var vm = await Loaded(service);
        vm.IsAwaitingConfirm = true;   // the countdown after an apply, without its timer

        Assert.False(vm.RefreshDisplaysCommand.CanExecute(null));
        service.DisplaysChanged += Raise.Event();
        service.Received(1).GetDisplays();
        Assert.Equal([Laptop], vm.Displays);

        vm.KeepSettingsCommand.Execute(null);
        await Settled(vm);

        service.Received(2).GetDisplays();
        Assert.Equal([Laptop, Monitor], vm.Displays);
        Assert.True(vm.RefreshDisplaysCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnOlderReadFinishingLast_DoesNotPutBackAScreenThatANewerReadFoundGone()
    {
        // A burst of display changes starts reads that overlap. The older one is held until the newer one is done,
        // and still lists the monitor that was unplugged since. Answered by how many reads came before, and the newer
        // read is started only once the older one has made its call, so the pool's order cannot swap them.
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var reads = 0;
        var service = Listing([Laptop, Monitor], [Laptop]);
        service.GetDisplays().Returns(_ =>
        {
            switch (Interlocked.Increment(ref reads))
            {
                case 1:
                    return [Laptop, Monitor];
                case 2:
                    entered.Set();
                    release.Wait(Bound);
                    return [Laptop, Monitor];
                default:
                    return [Laptop];
            }
        });
        try
        {
            var vm = await Loaded(service);

            service.DisplaysChanged += Raise.Event();
            var older = vm.InitializationComplete;
            Assert.True(entered.Wait(Bound), "the older read never started");
            service.DisplaysChanged += Raise.Event();
            await Settled(vm);
            Assert.Equal([Laptop], vm.Displays);   // the premise: the newer read is shown

            release.Set();
            await older;

            Assert.Equal([Laptop], vm.Displays);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Dispose_StopsListeningForDisplayChanges()
    {
        var service = Listing([Laptop], [Laptop]);
        var vm = await Loaded(service);

        vm.Dispose();

        service.Received(1).DisplaysChanged -= Arg.Any<EventHandler>();
    }
}
