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
}
