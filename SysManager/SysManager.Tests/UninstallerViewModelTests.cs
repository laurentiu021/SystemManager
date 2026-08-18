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
    private static UninstallerViewModel NewVm()
    {
        var viewModel = new UninstallerViewModel(new UninstallerService(new PowerShellRunner()));
        viewModel.IsElevated = false;
        return viewModel;
    }

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

    /// <summary>
    /// Typing in the search box must narrow the bound list AND correct the count and summary the user
    /// reads above it. Replaces a <c>FilterText</c> round-trip that only exercised the generated setter;
    /// <c>ApplyFilter</c> rebuilds <c>FilteredApps</c>, recomputes <c>AppCount</c> and rewrites
    /// <c>Summary</c>, and none of those three was asserted anywhere.
    /// </summary>
    [Fact]
    public void FilterText_NarrowsTheListAndCorrectsTheCountAndSummary()
    {
        var vm = NewVm();
        vm.AllApps.Clear();
        vm.AllApps.Add(new InstalledApp { Name = "Google Chrome", Id = "Google.Chrome" });
        vm.AllApps.Add(new InstalledApp { Name = "Notepad++", Id = "Notepad.Plus" });

        vm.FilterText = "chrome";

        Assert.Equal(["Google Chrome"], vm.FilteredApps.Select(a => a.Name).ToArray());
        Assert.Equal(1, vm.AppCount);
        Assert.Contains("of 2 total", vm.Summary);

        // Clearing it restores everything, and the summary drops the "(of N total)" qualifier because
        // nothing is filtered out any more.
        vm.FilterText = "";
        Assert.Equal(2, vm.FilteredApps.Count);
        Assert.Equal(2, vm.AppCount);
        Assert.DoesNotContain("of 2 total", vm.Summary);
    }

    /// <summary>The filter matches the package Id too, not just the display name.</summary>
    [Fact]
    public void FilterText_AlsoMatchesThePackageId()
    {
        var vm = NewVm();
        vm.AllApps.Clear();
        vm.AllApps.Add(new InstalledApp { Name = "Google Chrome", Id = "Google.Chrome" });
        vm.AllApps.Add(new InstalledApp { Name = "Notepad++", Id = "Notepad.Plus" });

        vm.FilterText = "Notepad.Plus";

        Assert.Equal(["Notepad++"], vm.FilteredApps.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void AppCount_DefaultZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.AppCount);
    }

    [Fact]
    public void UninstallCommand_DisabledWhenSysManagerIsElevated()
    {
        var viewModel = NewVm();
        viewModel.AllApps.Add(new InstalledApp { Name = "App", Id = "Vendor.App" });
        viewModel.FilterText = "App";

        Assert.True(viewModel.UninstallSelectedCommand.CanExecute(null));

        viewModel.IsElevated = true;

        Assert.False(viewModel.UninstallSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void DescribeUninstallFailure_KnownCodes()
    {
        Assert.Contains("Access denied", UninstallerViewModel.DescribeUninstallFailure(5, "Test"));
        Assert.Contains("cancelled", UninstallerViewModel.DescribeUninstallFailure(1602, "Test"));
    }

    [Fact]
    public void DescribeUninstallFailure_UnknownCode()
    {
        var result = UninstallerViewModel.DescribeUninstallFailure(9999, "Test");
        Assert.Contains("exit code 9999", result);
    }
}

/// <summary>
/// Confirmation-gate and terminal-status coverage for the Uninstaller. These tests
/// swap the process-wide <see cref="DialogService.Instance"/>, so they run in the
/// serialized "DialogService" collection. Process execution is substituted through
/// <see cref="IPowerShellRunner"/> so success, failure, and cancellation stay deterministic.
/// </summary>
[Collection("ProcessWideStatics")]
public class UninstallerViewModelGateTests
{
    private static UninstallerViewModel NewVm(IPowerShellRunner? runner = null)
    {
        var viewModel = new UninstallerViewModel(
            new UninstallerService(runner ?? new PowerShellRunner(), () => false));
        viewModel.IsElevated = false;
        return viewModel;
    }

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

    [Fact]
    public async Task UninstallSelected_AllSucceed_ReportsCompletion()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                "winget",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(0);
        var vm = NewVm(runner);
        Seed(vm, 1);
        vm.FilteredApps[0].Source = "winget";
        vm.FilteredApps[0].IsSelected = true;

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.UninstallSelectedCommand.ExecuteAsync(null);

            Assert.Equal(100, vm.Progress);
            Assert.Equal("Completed 1/1 uninstalls.", vm.StatusMessage);
            Assert.Empty(vm.AllApps);
        }
        finally
        {
            DialogService.Instance = previousDialog;
        }
    }

    [Fact]
    public async Task UninstallSelected_WhenRunnerFails_ReportsErrorsNotCompletion()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                "winget",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(5);
        var vm = NewVm(runner);
        Seed(vm, 1);
        vm.FilteredApps[0].Source = "winget";
        vm.FilteredApps[0].IsSelected = true;

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.UninstallSelectedCommand.ExecuteAsync(null);

            Assert.Equal(100, vm.Progress);
            Assert.Contains("finished with errors", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("failed 1", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Uninstall complete", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Windows UAC prompt", vm.FilteredApps[0].Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = previousDialog;
        }
    }

    [Theory]
    [InlineData(1641)]
    [InlineData(3010)]
    public async Task UninstallSelected_WhenRestartIsRequired_ReportsSuccessfulRemoval(int exitCode)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                "winget",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(exitCode);
        var vm = NewVm(runner);
        Seed(vm, 1);
        vm.FilteredApps[0].Source = "winget";
        vm.FilteredApps[0].IsSelected = true;

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.UninstallSelectedCommand.ExecuteAsync(null);

            Assert.Equal(100, vm.Progress);
            Assert.Contains("Completed 1/1", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("restart required", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(vm.AllApps);
        }
        finally
        {
            DialogService.Instance = previousDialog;
        }
    }

    [Fact]
    public async Task UninstallSelected_WhenCancelled_StopsBatchAndReportsPartialProgress()
    {
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                "winget",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(callInfo =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    return Task.FromResult(0);

                var token = callInfo.ArgAt<CancellationToken>(2);
                token.Register(() => pending.TrySetCanceled(token));
                started.TrySetResult(true);
                return pending.Task;
            });
        var vm = NewVm(runner);
        Seed(vm, 2);
        foreach (var app in vm.FilteredApps)
        {
            app.Source = "winget";
            app.IsSelected = true;
        }

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            var execution = vm.UninstallSelectedCommand.ExecuteAsync(null);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelCommand.Execute(null);
            await execution;

            Assert.Equal(50, vm.Progress);
            Assert.Contains("cancelled after 1/2 completed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Single(vm.FilteredApps);
            Assert.Equal("Cancelled", vm.FilteredApps[0].Status);
            await runner.Received(2).RunProcessAsync(
                "winget",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>());
        }
        finally
        {
            DialogService.Instance = previousDialog;
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
        // Uninstall additionally requires a populated list (HasApps) — see the empty-list
        // gate test below — so seed apps first to isolate the busy-gate behavior here.
        var vm = NewVm();
        Seed(vm, 3);
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

    [Fact]
    public void ListCommands_DisabledOnEmptyList_EnabledAfterScanPopulates()
    {
        // Uninstall / Select all / Deselect act on the listed apps, so on a fresh (unscanned)
        // list they must be disabled — a destructive Uninstall must never be clickable with
        // nothing to act on. Scan is the entry point and stays enabled.
        var vm = NewVm();
        Assert.Equal(0, vm.AppCount);
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        Assert.False(vm.SelectAllCommand.CanExecute(null));
        Assert.False(vm.DeselectAllCommand.CanExecute(null));

        Seed(vm, 3); // populates AllApps and refreshes AppCount via ApplyFilter
        Assert.True(vm.AppCount > 0);
        Assert.True(vm.UninstallSelectedCommand.CanExecute(null));
        Assert.True(vm.SelectAllCommand.CanExecute(null));
        Assert.True(vm.DeselectAllCommand.CanExecute(null));
    }
}
