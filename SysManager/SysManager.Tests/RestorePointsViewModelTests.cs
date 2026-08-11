// SysManager · RestorePointsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="RestorePointsViewModel"/>'s confirmation gate.
/// </summary>
/// <remarks>
/// Creating a checkpoint runs <c>Enable-ComputerRestore -Drive $env:SystemDrive</c> before
/// <c>Checkpoint-Computer</c>, because Windows refuses to create a restore point while System Protection
/// is off. That is a real, persistent change to system configuration — someone who deliberately turned
/// protection off (a common step on a small SSD, since it then reserves disk space indefinitely) got it
/// switched back on by a button that only advertised "create a restore point". Enabling it is the right
/// behaviour; doing it without saying so is not.
/// <para>The whole VM runs on a substituted <see cref="IPowerShellRunner"/>, so no PowerShell is
/// started and nothing on this machine's System Protection settings is touched.</para>
/// </remarks>
// Serialized: these swap the static DialogService.Instance, which is process-wide shared state.
// Required by ArchitectureTests.DialogServiceSwappers_AreInTheSerializedCollection.
[Collection("DialogService")]
public class RestorePointsViewModelTests
{
    private static RestorePointsViewModel NewVm(out IPowerShellRunner runner)
    {
        runner = Substitute.For<IPowerShellRunner>();
        return new RestorePointsViewModel(new RestorePointService(runner));
    }

    [Fact]
    public async Task Create_WhenUserDeclines_RunsNothing()
    {
        var vm = NewVm(out var runner);
        runner.ClearReceivedCalls();   // the constructor's initial list is not what this test is about

        using var dialog = new DialogAnswer(confirm: false);
        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.Calls);                      // the gate ran…
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);   // …and it blocked
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Create_TellsTheUserItMayTurnSystemProtectionBackOn()
    {
        // The disclosure IS the fix. A prompt that says only "create a restore point?" leaves the side
        // effect invisible, which is the state this test exists to prevent returning to.
        var vm = NewVm(out _);

        string? shown = null;
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Do<string>(m => shown = m), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            await vm.CreateCommand.ExecuteAsync(null);
        }
        finally { DialogService.Instance = prevDialog; }

        Assert.NotNull(shown);
        Assert.Contains("System Protection", shown!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("turn it", shown!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disk space", shown!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WhenConfirmed_StillCreates()
    {
        // The other half: the gate must not have turned the button into a no-op.
        var vm = NewVm(out var runner);
        runner.ClearReceivedCalls();

        using var dialog = new DialogAnswer(confirm: true);
        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.Calls);
        await runner.ReceivedWithAnyArgs().RunAsync(default!, default, default);
    }
}
