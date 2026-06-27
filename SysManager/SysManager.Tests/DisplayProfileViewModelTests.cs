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
}
