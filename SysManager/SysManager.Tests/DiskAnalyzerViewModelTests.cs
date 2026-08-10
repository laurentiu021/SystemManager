// SysManager · DiskAnalyzerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DiskAnalyzerViewModel"/>. Verifies initial state,
/// presets, and command availability.
/// </summary>
public class DiskAnalyzerViewModelTests
{
    // The VM resolves its preset paths asynchronously off the UI thread (DriveInfo probing
    // can stall, so it's moved off startup); wait for that init so the preset assertions
    // observe the populated collection instead of racing the background load.
    private static DiskAnalyzerViewModel NewVm()
    {
        var vm = new DiskAnalyzerViewModel(new Services.DiskAnalyzerService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.TotalSize);
        Assert.Equal(0, vm.TotalFiles);
        Assert.Equal(0, vm.TotalFolders);
        Assert.Equal(0, vm.EntryCount);
        Assert.Empty(vm.Entries);
        Assert.Contains("Select", vm.ScanSummary);
    }

    [Fact]
    public void Constructor_PresetPaths_NotEmpty()
    {
        var vm = NewVm();
        Assert.NotEmpty(vm.PresetPaths);
    }

    [Fact]
    public void Constructor_SelectedPath_IsSet()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedPath));
    }

    [Fact]
    public void Constructor_PresetPaths_ContainFixedDrives()
    {
        var vm = NewVm();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName);

        foreach (var drive in drives)
            Assert.Contains(vm.PresetPaths, p => p == drive);
    }

    [Fact]
    public void AnalyzeCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.AnalyzeCommand);
    }

    [Fact]
    public void CancelAnalysisCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.CancelAnalysisCommand);
    }

    [Fact]
    public void ShowInExplorerCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ShowInExplorerCommand);
    }

    [Fact]
    public void DrillDownCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.DrillDownCommand);
    }

    [Fact]
    public void GoUpCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.GoUpCommand);
    }

    [Fact]
    public void BrowseFolderCommand_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.BrowseFolderCommand);
    }

    [Fact]
    public void SelectedPath_CanBeChanged()
    {
        var vm = NewVm();
        vm.SelectedPath = @"C:\Test";
        Assert.Equal(@"C:\Test", vm.SelectedPath);
    }

    [Fact]
    public void HasDriveInfo_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.HasDriveInfo);
    }

    // ── Empty state distinguishes "not run yet" from "ran, found nothing" ───

    [Fact]
    public void BeforeAnyScan_EmptyState_TellsTheUserToScan()
    {
        var vm = NewVm();
        Assert.False(vm.HasScanned);
        Assert.Equal("No results yet", vm.EmptyTitle);
        Assert.Contains("analyze", vm.EmptyMessage);
    }

    [Fact]
    public async Task AfterAScanThatFoundNothing_EmptyState_StopsAskingForAScan()
    {
        // The overlay used to hardcode "No results yet … Pick a folder and analyze", so a
        // completed zero-result scan told the user to do the thing they had just done — while the
        // summary card next to it correctly said "No subfolders found." Scan a genuinely empty
        // directory and assert the two no longer contradict each other.
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vm = NewVm();
            vm.SelectedPath = dir;

            await vm.AnalyzeCommand.ExecuteAsync(null);

            Assert.Empty(vm.Entries);                       // nothing found, so the overlay shows
            Assert.True(vm.HasScanned);
            Assert.Equal("Nothing to show", vm.EmptyTitle);
            Assert.DoesNotContain("Pick a folder", vm.EmptyMessage);
            Assert.Equal("No subfolders found.", vm.ScanSummary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HasScanned_RaisesChangeNotificationsForTheEmptyStateText()
    {
        // The [NotifyPropertyChangedFor] attributes are what actually refresh the overlay; without
        // them the computed strings would change but the bound EmptyState would keep the old text.
        var vm = NewVm();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.HasScanned = true;

        Assert.Contains(nameof(vm.EmptyTitle), raised);
        Assert.Contains(nameof(vm.EmptyMessage), raised);
    }
}
