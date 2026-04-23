// SysManager · DiskAnalyzerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        var vm = new DiskAnalyzerViewModel();
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
        var vm = new DiskAnalyzerViewModel();
        Assert.NotEmpty(vm.PresetPaths);
    }

    [Fact]
    public void Constructor_SelectedPath_IsSet()
    {
        var vm = new DiskAnalyzerViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedPath));
    }

    [Fact]
    public void Constructor_PresetPaths_ContainFixedDrives()
    {
        var vm = new DiskAnalyzerViewModel();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName);

        foreach (var drive in drives)
            Assert.Contains(vm.PresetPaths, p => p == drive);
    }

    [Fact]
    public void AnalyzeCommand_Exists()
    {
        var vm = new DiskAnalyzerViewModel();
        Assert.NotNull(vm.AnalyzeCommand);
    }

    [Fact]
    public void CancelAnalysisCommand_Exists()
    {
        var vm = new DiskAnalyzerViewModel();
        Assert.NotNull(vm.CancelAnalysisCommand);
    }

    [Fact]
    public void ShowInExplorerCommand_Exists()
    {
        var vm = new DiskAnalyzerViewModel();
        Assert.NotNull(vm.ShowInExplorerCommand);
    }

    [Fact]
    public void DrillDownCommand_Exists()
    {
        var vm = new DiskAnalyzerViewModel();
        Assert.NotNull(vm.DrillDownCommand);
    }

    [Fact]
    public void GoUpCommand_Exists()
    {
        var vm = new DiskAnalyzerViewModel();
        Assert.NotNull(vm.GoUpCommand);
    }

    [Fact]
    public void BrowseFolderCommand_Exists()
    {
        var vm = new DiskAnalyzerViewModel();
        Assert.NotNull(vm.BrowseFolderCommand);
    }

    [Fact]
    public void SelectedPath_CanBeChanged()
    {
        var vm = new DiskAnalyzerViewModel();
        vm.SelectedPath = @"C:\Test";
        Assert.Equal(@"C:\Test", vm.SelectedPath);
    }

    [Fact]
    public void HasDriveInfo_DefaultFalse()
    {
        var vm = new DiskAnalyzerViewModel();
        Assert.False(vm.HasDriveInfo);
    }
}
