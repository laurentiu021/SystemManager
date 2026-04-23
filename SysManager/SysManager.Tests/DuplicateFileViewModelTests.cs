// SysManager · DuplicateFileViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DuplicateFileViewModel"/>. Verifies initial state,
/// preset folders, and FormatSize logic.
/// </summary>
public class DuplicateFileViewModelTests
{
    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        var vm = new DuplicateFileViewModel();
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.GroupCount);
        Assert.Equal(0, vm.DuplicateFileCount);
        Assert.Equal(0, vm.TotalWasted);
        Assert.Equal(1, vm.MinSizeKb);
        Assert.Empty(vm.Groups);
        Assert.Contains("Select a folder", vm.ScanSummary);
    }

    [Fact]
    public void Constructor_PresetFolders_NotEmpty()
    {
        var vm = new DuplicateFileViewModel();
        Assert.NotEmpty(vm.PresetFolders);
    }

    [Fact]
    public void Constructor_SelectedFolder_IsSet()
    {
        var vm = new DuplicateFileViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedFolder));
    }

    [Fact]
    public void Constructor_PresetFolders_ContainUserProfile()
    {
        var vm = new DuplicateFileViewModel();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(vm.PresetFolders, f => f == userProfile);
    }

    [Fact]
    public void Constructor_PresetFolders_ContainFixedDrives()
    {
        var vm = new DuplicateFileViewModel();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName);

        foreach (var drive in drives)
            Assert.Contains(vm.PresetFolders, f => f == drive);
    }

    [Fact]
    public void ScanCommand_Exists()
    {
        var vm = new DuplicateFileViewModel();
        Assert.NotNull(vm.ScanCommand);
    }

    [Fact]
    public void CancelScanCommand_Exists()
    {
        var vm = new DuplicateFileViewModel();
        Assert.NotNull(vm.CancelScanCommand);
    }

    [Fact]
    public void ShowInExplorerCommand_Exists()
    {
        var vm = new DuplicateFileViewModel();
        Assert.NotNull(vm.ShowInExplorerCommand);
    }

    [Fact]
    public void CopyPathCommand_Exists()
    {
        var vm = new DuplicateFileViewModel();
        Assert.NotNull(vm.CopyPathCommand);
    }

    [Fact]
    public void BrowseFolderCommand_Exists()
    {
        var vm = new DuplicateFileViewModel();
        Assert.NotNull(vm.BrowseFolderCommand);
    }

    [Fact]
    public void MinSizeKb_CanBeChanged()
    {
        var vm = new DuplicateFileViewModel();
        vm.MinSizeKb = 500;
        Assert.Equal(500, vm.MinSizeKb);
    }

    [Fact]
    public void SelectedFolder_CanBeChanged()
    {
        var vm = new DuplicateFileViewModel();
        vm.SelectedFolder = @"C:\Test";
        Assert.Equal(@"C:\Test", vm.SelectedFolder);
    }

    // ── DuplicateFileEntry model ──

    [Fact]
    public void DuplicateFileEntry_DefaultValues()
    {
        var entry = new DuplicateFileEntry();
        Assert.Equal("", entry.Path);
        Assert.Equal("", entry.Name);
        Assert.Equal(0, entry.SizeBytes);
        Assert.False(entry.IsSelected);
    }

    [Fact]
    public void DuplicateFileEntry_PropertyChange_Notifies()
    {
        var entry = new DuplicateFileEntry();
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        entry.Name = "test.bin";
        entry.Path = @"C:\test.bin";
        entry.SizeBytes = 1024;
        entry.IsSelected = true;

        Assert.Contains("Name", changed);
        Assert.Contains("Path", changed);
        Assert.Contains("SizeBytes", changed);
        Assert.Contains("IsSelected", changed);
    }

    // ── DuplicateFileGroup model ──

    [Fact]
    public void DuplicateFileGroup_PropertyChange_Notifies()
    {
        var group = new DuplicateFileGroup();
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        group.Hash = "ABC123";
        group.FileSize = 2048;
        group.Count = 3;

        Assert.Contains("Hash", changed);
        Assert.Contains("FileSize", changed);
        Assert.Contains("Count", changed);
    }

    [Fact]
    public void DuplicateFileGroup_Files_IsObservable()
    {
        var group = new DuplicateFileGroup();
        Assert.NotNull(group.Files);
        Assert.Empty(group.Files);

        group.Files.Add(new DuplicateFileEntry { Name = "test.bin" });
        Assert.Single(group.Files);
    }
}
