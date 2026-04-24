// SysManager · StartupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupViewModel"/>. Verifies initial state,
/// commands, and scan summary logic.
/// </summary>
public class StartupViewModelTests
{
    [Fact]
    public void Constructor_EntriesCollectionNotNull()
    {
        var vm = new StartupViewModel();
        Assert.NotNull(vm.Entries);
    }

    [Fact]
    public void Constructor_CommandsExist()
    {
        var vm = new StartupViewModel();
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.ToggleEntryCommand);
        Assert.NotNull(vm.EnableAllCommand);
        Assert.NotNull(vm.OpenFileLocationCommand);
    }

    [Fact]
    public void Constructor_DefaultCounts()
    {
        var vm = new StartupViewModel();
        // Before scan completes, counts should be 0
        Assert.Equal(0, vm.EnabledCount);
        Assert.Equal(0, vm.DisabledCount);
        Assert.Equal(0, vm.TotalCount);
    }

    [Fact]
    public void ScanSummary_HasDefaultValue()
    {
        var vm = new StartupViewModel();
        Assert.False(string.IsNullOrEmpty(vm.ScanSummary));
    }

    [Fact]
    public async Task ScanAsync_PopulatesEntries()
    {
        var vm = new StartupViewModel();
        // Constructor fires auto-scan. Poll until it completes (up to 15s).
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            if (!vm.IsBusy) break;
        }
        // On any Windows machine there should be at least 1 startup item.
        Assert.True(vm.Entries.Count > 0, "Expected at least one startup entry");
        Assert.True(vm.TotalCount > 0);
    }

    [Fact]
    public async Task ScanAsync_UpdatesScanSummary()
    {
        var vm = new StartupViewModel();
        // Constructor fires auto-scan. Poll until it completes (up to 15s).
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            if (!vm.IsBusy) break;
        }
        // After scan, summary should contain counts if entries were found
        if (vm.TotalCount > 0)
            Assert.Contains("enabled", vm.ScanSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_CountsAreConsistent()
    {
        var vm = new StartupViewModel();
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            if (!vm.IsBusy) break;
        }
        Assert.Equal(vm.Entries.Count, vm.TotalCount);
        Assert.Equal(vm.EnabledCount + vm.DisabledCount, vm.TotalCount);
    }

    [Fact]
    public void ToggleEntry_NullDoesNotThrow()
    {
        var vm = new StartupViewModel();
        var ex = Record.Exception(() => vm.ToggleEntryCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenFileLocation_NullDoesNotThrow()
    {
        var vm = new StartupViewModel();
        var ex = Record.Exception(() => vm.OpenFileLocationCommand.Execute(null));
        Assert.Null(ex);
    }
}
