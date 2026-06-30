// SysManager · DefenderViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DefenderViewModel"/> — focuses on the re-entrancy guard
/// (NotBusy) that serialises the mutating Defender commands so two overlapping
/// Set-MpPreference operations can't race the read-back verification.
/// </summary>
[Collection("DialogService")]
public class DefenderViewModelTests
{
    private static DefenderViewModel NewVm()
    {
        // Runner returns an empty result set, so GetStatusAsync produces a default
        // (unavailable) status without touching real PowerShell/Defender.
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<System.Threading.CancellationToken>())
          .Returns(new Collection<PSObject>());
        var vm = new DefenderViewModel(new DefenderService(ps));
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void AfterInit_NotBusy_IsTrue()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
        Assert.True(vm.NotBusy);
    }

    [Fact]
    public void MutatingCommands_AreGatedOnNotBusy()
    {
        var vm = NewVm();

        // While not busy, the always-available mutating commands can run.
        Assert.True(vm.TogglePuaCommand.CanExecute(null));
        Assert.True(vm.ToggleCfaCommand.CanExecute(null));
        Assert.True(vm.AddExclusionCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));

        // Simulate an in-flight operation: every mutating command must refuse to start.
        vm.IsBusy = true;
        Assert.False(vm.NotBusy);
        Assert.False(vm.TogglePuaCommand.CanExecute(null));
        Assert.False(vm.ToggleCfaCommand.CanExecute(null));
        Assert.False(vm.AddExclusionCommand.CanExecute(null));
        Assert.False(vm.RemoveExclusionCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));

        // Clearing busy re-enables them.
        vm.IsBusy = false;
        Assert.True(vm.TogglePuaCommand.CanExecute(null));
    }

    [Fact]
    public void RemoveExclusion_RequiresBothSelectionAndNotBusy()
    {
        var vm = NewVm();

        // No selection -> cannot remove even when idle.
        Assert.Null(vm.SelectedExclusion);
        Assert.False(vm.RemoveExclusionCommand.CanExecute(null));

        // With a selection and idle -> can remove.
        vm.SelectedExclusion = @"C:\some\folder";
        Assert.True(vm.RemoveExclusionCommand.CanExecute(null));

        // Busy overrides the selection -> cannot remove.
        vm.IsBusy = true;
        Assert.False(vm.RemoveExclusionCommand.CanExecute(null));
    }

    [Fact]
    public async Task TogglePua_Off_WhenStatusUnavailable_ReportsFailureNotSuccess()
    {
        // Regression: a disable-toggle targets PuaProtection==0. If the Set silently fails
        // (needs admin / PS fault) the service returns the all-zeros Unavailable status,
        // whose PuaProtection is also 0 — which would FALSELY satisfy the read-back check.
        // The verdict must require status.Available, so this reports "not changed", not
        // "updated".
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<System.Threading.CancellationToken>())
          .Returns(new Collection<PSObject>()); // empty -> DefenderStatus.Unavailable (all zeros)
        var vm = new DefenderViewModel(new DefenderService(ps));
        await vm.InitializationComplete;

        // Pretend PUA was on so the toggle requests OFF (target 0), the dangerous case.
        vm.PuaEnabled = true;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // auto-confirm
        DialogService.Instance = dialog;
        try
        {
            await vm.TogglePuaCommand.ExecuteAsync(null);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }

        Assert.Contains("was not changed", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updated", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TogglePua_WhenRunspaceFaults_SurfacesStatus_DoesNotThrow()
    {
        // Regression (idx 74): a PowerShell runspace-level fault during a mutating
        // command (not the script RuntimeException the service catches) must be caught
        // and surfaced as a status message, like RefreshAsync — not escape the async
        // command to the global handler. The first RunAsync (GetStatus at init) succeeds;
        // the Set call faults.
        var ps = Substitute.For<IPowerShellRunner>();
        var calls = 0;
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<System.Threading.CancellationToken>())
          .Returns(_ => ++calls <= 1
              ? new Collection<PSObject>()
              : throw new System.InvalidOperationException("runspace is broken"));
        var vm = new DefenderViewModel(new DefenderService(ps));
        await vm.InitializationComplete;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            // Must NOT throw — the fix adds the catch clauses.
            await vm.TogglePuaCommand.ExecuteAsync(null);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }

        Assert.False(vm.IsBusy);
        Assert.Contains("could not change pua", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
    }
}
