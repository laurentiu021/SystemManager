// SysManager · NetworkRepairViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the flush-DNS gate test swaps the static DialogService.Instance.
[Collection("ProcessWideStatics")]
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
    public void Repairing_DrivesIsBusy_SoTheSidebarShowsProgress()
    {
        // NavItem forwards ViewModelBase.IsBusy to the slim progress bar under the tab's name, which is the
        // only sign — while the user is looking at another tab — that this one is working. Five tabs kept a
        // running flag and never assigned IsBusy, so their bar never appeared; this asserts the generated
        // On…Changed hook actually fires, which the source-shape guard in ArchitectureTests cannot.
        //
        // One behaviour test for the mechanism rather than five identical ones: the other four are the same
        // one-line shape, and EveryViewModelThatTracksRunningState_ForwardsItToIsBusy is what keeps them there.
        var vm = new NetworkRepairViewModel(NewShared());
        Assert.False(vm.IsBusy);

        vm.IsRepairing = true;
        Assert.True(vm.IsBusy, "a repair is running and the shell was never told");

        vm.IsRepairing = false;
        Assert.False(vm.IsBusy, "the bar has to clear when the work finishes, or the tab looks stuck");
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
