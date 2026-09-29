// SysManager · BrowserCleanerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for the toast <see cref="BrowserCleanerViewModel"/> shows after a clean (#2456). The rescan replaces the
/// status line at once, so the toast is often the only result the user sees.
/// </summary>
// Serialized: CleanAsync_WhileDiskCategoryLocked swaps the static DialogService.Instance and touches the
// process-wide OperationLockService.Instance. Required by
// ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection.
[Collection("ProcessWideStatics")]
public class BrowserCleanerViewModelTests
{
    // Points the service at an empty temp tree rather than the real profile folders, so the scan this VM
    // runs on construction finds nothing real and completes fast — no browser needs to be installed here.
    private static BrowserCleanerViewModel NewVm()
    {
        var root = Path.Combine(Path.GetTempPath(), "smtest_browsercleaner_" + Guid.NewGuid().ToString("N"));
        var vm = new BrowserCleanerViewModel(new BrowserCleanerService(
            Path.Combine(root, "Local"), Path.Combine(root, "Roaming")));
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public async Task CleanAsync_WhileDiskCategoryLocked_RefusesAndCleansNothing()
    {
        // #2510. A deletion batch that is cancelled with nothing to name it when SysManager closes
        // mid-run now shares the disk-scanning/deleting tabs' lock.
        var vm = NewVm();
        vm.Items.Add(new BrowserCleanupItem
        {
            Browser = "Test Browser",
            Category = "Cache",
            Description = "test",
            Paths = [],
            IsSelected = true,
        });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup");
        Assert.NotNull(held);
        try
        {
            await vm.CleanCommand.ExecuteAsync(null);

            Assert.Equal("Cannot start — Deep Cleanup is already running.", vm.StatusMessage);
            Assert.False(vm.IsBusy);
            Assert.Single(vm.Items);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void CleanToast_WhenNothingWasRemoved_DoesNotSayTheDataWasCleaned()
    {
        // An open browser can hold every file. The toast said "Browser data cleaned — 0 files removed."
        var (title, detail) = BrowserCleanerViewModel.CleanToast(0);

        Assert.Equal("Nothing was removed", title);
        Assert.Contains("Close it and clean again", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanToast_WhenFilesWereRemoved_SaysHowMany()
        => Assert.Equal(("Browser data cleaned", "12 files removed."), BrowserCleanerViewModel.CleanToast(12));
}
