// SysManager · LargeFilesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Integration coverage for <see cref="LargeFilesViewModel"/> against the real filesystem. Moved here with
/// the feature when it left Deep Cleanup (#1523).
/// </summary>
public class LargeFilesViewModelTests
{
    [Fact]
    public void ScanStatus_StartsEmpty()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        Assert.Equal(string.Empty, vm.ScanStatus);
    }

    [Fact]
    public void Files_StartsEmpty()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        Assert.Empty(vm.Files);
    }

    [Fact]
    public void MinSizeMB_DefaultsTo500()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        Assert.Equal(500, vm.MinSizeMB);
    }

    [Fact]
    public void TopCount_DefaultsTo100()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        Assert.Equal(100, vm.TopCount);
    }

    [Fact]
    public void IsScanning_DefaultsFalse()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        Assert.False(vm.IsScanning);
    }

    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("CancelCommand")]
    [InlineData("ShowInExplorerCommand")]
    [InlineData("CopyPathCommand")]
    public void CommandExists(string propertyName)
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        var prop = vm.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    [Fact]
    public void CopyPathCommand_Null_DoesNotThrow()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        var ex = Record.Exception(() => vm.CopyPathCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void ShowInExplorerCommand_NullPath_DoesNotThrow()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void ShowInExplorerCommand_NonExistent_DoesNotThrow()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService());
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(@"C:\no_such_file_" + Guid.NewGuid().ToString("N")));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Scan_NoLocation_ReportsError()
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService()) { SelectedLocation = null };
        var t = vm.ScanCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        Assert.Contains("location", vm.ScanStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scan_ValidLocation_PopulatesResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerVmTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "x.bin"), new byte[2 * 1024 * 1024]);
        try
        {
            var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService())
            {
                SelectedLocation = new ScanLocation("Test", root),
                MinSizeMB = 1,
                TopCount = 10
            };
            var t = vm.ScanCommand.ExecuteAsync(null);
            if (t is Task tt) await tt;
            Assert.NotEmpty(vm.Files);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1024)]
    [InlineData(10_000)]
    public void MinSizeMB_Settable(int v)
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService()) { MinSizeMB = v };
        Assert.Equal(v, vm.MinSizeMB);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    public void TopCount_Settable(int v)
    {
        var vm = new LargeFilesViewModel(new LargeFileScanner(), new FixedDriveService()) { TopCount = v };
        Assert.Equal(v, vm.TopCount);
    }
}
