// SysManager · StartupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupViewModel"/>. Verifies initial state,
/// commands, and scan summary logic.
/// Serialized under the DialogService collection: the confirm-gate test swaps the
/// process-wide static <see cref="DialogService.Instance"/>.
/// </summary>
[Collection("ProcessWideStatics")]
public class StartupViewModelTests
{
    [Fact]
    public void Constructor_EntriesCollectionNotNull()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        Assert.NotNull(vm.Entries);
    }

    [Fact]
    public void Constructor_CommandsExist()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.ToggleEntryCommand);
        Assert.NotNull(vm.EnableAllCommand);
        Assert.NotNull(vm.OpenFileLocationCommand);
    }

    [Fact]
    public void Constructor_DefaultCounts()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        // Before scan completes, counts should be 0
        Assert.Equal(0, vm.EnabledCount);
        Assert.Equal(0, vm.DisabledCount);
        Assert.Equal(0, vm.TotalCount);
    }

    [Fact]
    public void ScanSummary_HasDefaultValue()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        Assert.False(string.IsNullOrEmpty(vm.ScanSummary));
    }

    [Fact]
    public async Task ScanAsync_PopulatesEntries()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        // The constructor fires the scan and forgets it; this is the task it started.
        await vm.InitializationComplete;
        // On any Windows machine there should be at least 1 startup item.
        Assert.True(vm.Entries.Count > 0, "Expected at least one startup entry");
        Assert.True(vm.TotalCount > 0);
    }

    [Fact]
    public async Task ScanAsync_UpdatesScanSummary()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        // The constructor fires the scan and forgets it; this is the task it started.
        await vm.InitializationComplete;
        // After scan, summary should contain counts if entries were found
        if (vm.TotalCount > 0)
            Assert.Contains("enabled", vm.ScanSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_CountsAreConsistent()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        // The constructor fires the scan and forgets it; this is the task it started.
        await vm.InitializationComplete;
        Assert.Equal(vm.Entries.Count, vm.TotalCount);
        Assert.Equal(vm.EnabledCount + vm.DisabledCount, vm.TotalCount);
    }

    [Fact]
    public void ToggleEntry_NullDoesNotThrow()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        var ex = Record.Exception(() => vm.ToggleEntryCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void ToggleEntry_WrongTypeDoesNotThrow()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        // Simulates WPF DataGrid virtualization passing a non-StartupEntry object
        var ex = Record.Exception(() => vm.ToggleEntryCommand.Execute("not a StartupEntry"));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenFileLocation_NullDoesNotThrow()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        var ex = Record.Exception(() => vm.OpenFileLocationCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenFileLocation_WrongTypeDoesNotThrow()
    {
        var vm = new StartupViewModel(new Services.StartupService());
        // Simulates WPF DataGrid virtualization passing a non-StartupEntry object
        var ex = Record.Exception(() => vm.OpenFileLocationCommand.Execute(42));
        Assert.Null(ex);
    }

    // ── re-entrancy guard (regression: overlapping registry writes) ──

    // ── confirmation gate (regression: bulk Enable All is a system change, must confirm) ──

    [Fact]
    public async Task EnableAll_WhenUserDeclinesConfirm_DoesNotEnableAndLeavesEntriesDisabled()
    {
        // Regression: "Enable All" re-arms every disabled startup item (registry/task writes)
        // and adds boot time, so it must ask first. Declining must short-circuit BEFORE any
        // write — proven here by the entry staying disabled (SetEnabledAsync is never reached).
        var vm = new StartupViewModel(new StartupService());
        // Wait for the ctor's auto-scan to finish before seeding, so it cannot overwrite the seeded
        // entry mid-test. The task itself, not a sampled IsBusy flag.
        await vm.InitializationComplete;

        vm.Entries.Clear();
        var disabled = new StartupEntry { Name = "Seeded", Command = "c:\\x.exe", IsEnabled = false };
        vm.Entries.Add(disabled);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            await vm.EnableAllCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.False(disabled.IsEnabled); // never enabled — the write path was not taken
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public async Task EnableAll_WithNoDisabledEntries_DoesNotPrompt()
    {
        // Nothing to enable → no confirmation dialog (and no write). Guards against nagging.
        var vm = new StartupViewModel(new StartupService());
        // Wait for the ctor's auto-scan to finish before seeding, so it cannot overwrite the seeded
        // entry mid-test. The task itself, not a sampled IsBusy flag.
        await vm.InitializationComplete;

        vm.Entries.Clear();
        vm.Entries.Add(new StartupEntry { Name = "On", Command = "c:\\y.exe", IsEnabled = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            await vm.EnableAllCommand.ExecuteAsync(null);
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void StateChangingCommands_DisabledWhileBusy()
    {
        // Scan, EnableAll and ToggleEntry all read or write the same startup registry/task
        // state; the NotBusy gate stops them overlapping and interleaving registry writes.
        // Drive IsBusy explicitly rather than asserting the post-construction baseline: the
        // constructor kicks off an async auto-scan that briefly sets IsBusy itself.
        var vm = new StartupViewModel(new Services.StartupService());

        vm.IsBusy = true;
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.EnableAllCommand.CanExecute(null));
        Assert.False(vm.ToggleEntryCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.EnableAllCommand.CanExecute(null));
        Assert.True(vm.ToggleEntryCommand.CanExecute(null));
    }
}
