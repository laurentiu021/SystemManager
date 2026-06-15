// SysManager · NetworkRepairViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the flush-DNS gate test swaps the static DialogService.Instance.
[Collection("DialogService")]
public class NetworkRepairViewModelTests
{
    private static NetworkSharedState NewShared() =>
        new(new PingMonitorService(), new TracerouteService(), new TracerouteMonitorService(),
            new SpeedTestService(), new NetworkRepairService(new PowerShellRunner()));

    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = NewShared();
        var vm = new NetworkRepairViewModel(shared);
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultState_NotRepairing()
    {
        var shared = NewShared();
        var vm = new NetworkRepairViewModel(shared);
        Assert.False(vm.IsRepairing);
        Assert.Equal("", vm.RepairStatus);
        Assert.False(vm.RepairNeedsReboot);
    }

    [Fact]
    public async Task FlushDns_WhenUserDeclinesConfirm_DoesNothing()
    {
        var vm = new NetworkRepairViewModel(NewShared());

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            await vm.FlushDnsCommand.ExecuteAsync(null);

            // Declining must prompt once and then short-circuit before any repair runs:
            // status stays empty and the VM never enters the repairing state.
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Equal("", vm.RepairStatus);
            Assert.False(vm.IsRepairing);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }
}
