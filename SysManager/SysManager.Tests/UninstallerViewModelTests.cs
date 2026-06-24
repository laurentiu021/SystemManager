// SysManager · UninstallerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UninstallerViewModel"/>. Verifies initial state,
/// commands, and filter logic. Sorting is handled by DataGrid column headers.
/// </summary>
public class UninstallerViewModelTests
{
    private static UninstallerViewModel NewVm() => new(new UninstallerService(new PowerShellRunner()));

    [Fact]
    public void Constructor_Commands_Exist()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.UninstallSelectedCommand);
        Assert.NotNull(vm.CancelCommand);
        Assert.NotNull(vm.SelectAllCommand);
        Assert.NotNull(vm.DeselectAllCommand);
    }

    [Fact]
    public void Constructor_Collections_NotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.AllApps);
        Assert.NotNull(vm.FilteredApps);
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void FilterText_DefaultEmpty()
    {
        var vm = NewVm();
        Assert.Equal("", vm.FilterText);
    }

    [Fact]
    public void Summary_HasDefaultValue()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    [Fact]
    public void FilterText_CanBeChanged()
    {
        var vm = NewVm();
        vm.FilterText = "chrome";
        Assert.Equal("chrome", vm.FilterText);
    }

    [Fact]
    public void AppCount_DefaultZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.AppCount);
    }

    [Fact]
    public void DescribeUninstallFailure_KnownCodes()
    {
        Assert.Contains("Access denied", UninstallerViewModel.DescribeUninstallFailure(5, "Test"));
        Assert.Contains("cancelled", UninstallerViewModel.DescribeUninstallFailure(1602, "Test"));
        Assert.Contains("reboot", UninstallerViewModel.DescribeUninstallFailure(3010, "Test"));
    }

    [Fact]
    public void DescribeUninstallFailure_UnknownCode()
    {
        var result = UninstallerViewModel.DescribeUninstallFailure(9999, "Test");
        Assert.Contains("exit code 9999", result);
    }
}

/// <summary>
/// Confirmation-gate coverage for the Uninstaller. These swap the process-wide
/// <see cref="DialogService.Instance"/>, so they run in the serialized
/// "DialogService" collection. <see cref="UninstallerService"/> takes a concrete
/// <see cref="PowerShellRunner"/> (not mockable), so the "confirm DOES uninstall"
/// direction belongs in integration tests; here we cover the decline path and the
/// pure-VM select-all guard, which are fully deterministic.
/// </summary>
[Collection("DialogService")]
public class UninstallerViewModelGateTests
{
    private static UninstallerViewModel NewVm() => new(new UninstallerService(new PowerShellRunner()));

    // Populate FilteredApps deterministically through the public filter path:
    // add to AllApps, then toggle FilterText so ApplyFilter() repopulates.
    private static void Seed(UninstallerViewModel vm, int count)
    {
        for (int i = 0; i < count; i++)
            vm.AllApps.Add(new InstalledApp { Name = $"app{i:000}", Id = $"id{i:000}" });
        vm.FilterText = "app"; // matches all → triggers ApplyFilter
        vm.FilterText = "";    // back to empty (the SelectAll guard requires empty filter)
    }

    // ── UninstallSelected (permanent, non-undoable batch removal) ─────────

    [Fact]
    public void UninstallSelected_WhenUserDeclinesConfirm_RemovesNothing()
    {
        var vm = NewVm();
        Seed(vm, 3);
        vm.FilteredApps[0].IsSelected = true;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // "No"
        DialogService.Instance = dialog;
        try
        {
            var countBefore = vm.FilteredApps.Count;
            vm.UninstallSelectedCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining must short-circuit before any uninstall: the list is intact
            // and the VM never entered the busy/uninstalling state.
            Assert.Equal(countBefore, vm.FilteredApps.Count);
            Assert.False(vm.IsBusy);
            Assert.DoesNotContain("Uninstalling", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void UninstallSelected_WithNoSelection_NeverPromptsConfirm()
    {
        var vm = NewVm();
        Seed(vm, 3); // none selected

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            vm.UninstallSelectedCommand.Execute(null);

            // Nothing selected → the destructive prompt must not appear.
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Contains("No apps selected", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── SelectAll bulk guard (>20 apps, no active filter) ─────────────────

    [Fact]
    public void SelectAll_Over20AppsNoFilter_WhenUserDeclines_SelectsNothing()
    {
        var vm = NewVm();
        Seed(vm, 21); // > 20, filter empty → guard fires

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // "No"
        DialogService.Instance = dialog;
        try
        {
            vm.SelectAllCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.All(vm.FilteredApps, a => Assert.False(a.IsSelected));
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void SelectAll_Over20AppsNoFilter_WhenUserConfirms_SelectsAll()
    {
        var vm = NewVm();
        Seed(vm, 21);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // "Yes"
        DialogService.Instance = dialog;
        try
        {
            vm.SelectAllCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.All(vm.FilteredApps, a => Assert.True(a.IsSelected));
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void SelectAll_AtMost20Apps_SkipsGuardAndSelectsAll()
    {
        var vm = NewVm();
        Seed(vm, 5); // <= 20 → no guard

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            vm.SelectAllCommand.Execute(null);

            // Small list → the bulk-select guard must not prompt at all.
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.All(vm.FilteredApps, a => Assert.True(a.IsSelected));
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── re-entrancy guard (regression: shared CTS disposed mid-flight) ──

    [Fact]
    public void LongRunningCommands_DisabledWhileBusy()
    {
        // Scan and UninstallSelected both recreate the shared _cts. Without the NotBusy gate,
        // triggering one while the other runs would dispose the CTS still being awaited
        // (ObjectDisposedException). Cancel must stay enabled so an in-flight run can stop.
        var vm = NewVm();
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.UninstallSelectedCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.UninstallSelectedCommand.CanExecute(null));
    }
}
