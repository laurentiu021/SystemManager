using System.IO;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class DeepCleanupViewModelTests
{
    [Fact]
    public void Constructs()
    {
        var vm = new DeepCleanupViewModel();
        Assert.NotNull(vm);
    }

    [Fact]
    public void InitialSummary_IsNonEmpty()
    {
        var vm = new DeepCleanupViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.ScanSummary));
    }

    [Fact]
    public void CleanSummary_StartsEmpty()
    {
        var vm = new DeepCleanupViewModel();
        Assert.Equal(string.Empty, vm.CleanSummary);
    }

    [Fact]
    public void LargeScanStatus_StartsEmpty()
    {
        var vm = new DeepCleanupViewModel();
        Assert.Equal(string.Empty, vm.LargeScanStatus);
    }

    [Fact]
    public void Categories_StartsEmpty()
    {
        var vm = new DeepCleanupViewModel();
        Assert.Empty(vm.Categories);
    }

    [Fact]
    public void LargeFiles_StartsEmpty()
    {
        var vm = new DeepCleanupViewModel();
        Assert.Empty(vm.LargeFiles);
    }

    [Fact]
    public void MinSizeMB_DefaultsTo500()
    {
        var vm = new DeepCleanupViewModel();
        Assert.Equal(500, vm.MinSizeMB);
    }

    [Fact]
    public void TopCount_DefaultsTo100()
    {
        var vm = new DeepCleanupViewModel();
        Assert.Equal(100, vm.TopCount);
    }

    [Fact]
    public void IsScanning_DefaultsFalse()
    {
        var vm = new DeepCleanupViewModel();
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void IsCleaning_DefaultsFalse()
    {
        var vm = new DeepCleanupViewModel();
        Assert.False(vm.IsCleaning);
    }

    [Fact]
    public void IsLargeScanning_DefaultsFalse()
    {
        var vm = new DeepCleanupViewModel();
        Assert.False(vm.IsLargeScanning);
    }

    [Fact]
    public void TotalSelectedBytes_StartsZero()
    {
        var vm = new DeepCleanupViewModel();
        Assert.Equal(0, vm.TotalSelectedBytes);
    }

    [Fact]
    public void TotalSelectedDisplay_StartsWithZeroBytes()
    {
        var vm = new DeepCleanupViewModel();
        Assert.StartsWith("0", vm.TotalSelectedDisplay);
    }

    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("CleanCommand")]
    [InlineData("SelectAllCommand")]
    [InlineData("CancelCommand")]
    [InlineData("ScanLargeFilesCommand")]
    [InlineData("ShowInExplorerCommand")]
    [InlineData("CopyPathCommand")]
    public void CommandExists(string propertyName)
    {
        var vm = new DeepCleanupViewModel();
        var prop = vm.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    [Fact]
    public async Task ScanCommand_PopulatesCategories()
    {
        var vm = new DeepCleanupViewModel();
        var task = vm.ScanCommand.ExecuteAsync(null);
        if (task is Task t) await t;
        Assert.True(vm.Categories.Count >= 10);
    }

    [Fact]
    public async Task ScanCommand_UpdatesScanSummary()
    {
        var vm = new DeepCleanupViewModel();
        var before = vm.ScanSummary;
        var task = vm.ScanCommand.ExecuteAsync(null);
        if (task is Task t) await t;
        Assert.NotEqual(before, vm.ScanSummary);
    }

    [Fact]
    public async Task SelectAllCommand_True_SelectsNonDestructive()
    {
        var vm = new DeepCleanupViewModel();
        var t = vm.ScanCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;

        vm.SelectAllCommand.Execute(true);
        // Windows.old must stay unselected.
        foreach (var c in vm.Categories)
        {
            if (c.IsDestructiveHint) Assert.False(c.IsSelected);
            else Assert.True(c.IsSelected);
        }
    }

    [Fact]
    public async Task SelectAllCommand_False_DeselectsEverything()
    {
        var vm = new DeepCleanupViewModel();
        var t = vm.ScanCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        vm.SelectAllCommand.Execute(false);
        foreach (var c in vm.Categories) Assert.False(c.IsSelected);
    }

    [Fact]
    public void CopyPathCommand_Null_DoesNotThrow()
    {
        var vm = new DeepCleanupViewModel();
        var ex = Record.Exception(() => vm.CopyPathCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void ShowInExplorerCommand_NullPath_DoesNotThrow()
    {
        var vm = new DeepCleanupViewModel();
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void ShowInExplorerCommand_NonExistent_DoesNotThrow()
    {
        var vm = new DeepCleanupViewModel();
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(@"C:\no_such_file_" + Guid.NewGuid().ToString("N")));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCommand_DoesNotThrow()
    {
        var vm = new DeepCleanupViewModel();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ScanLargeFiles_NoLocation_ReportsError()
    {
        var vm = new DeepCleanupViewModel { SelectedLocation = null };
        var t = vm.ScanLargeFilesCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        Assert.Contains("location", vm.LargeScanStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanLargeFiles_ValidLocation_PopulatesResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerVmTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "x.bin"), new byte[2 * 1024 * 1024]);
        try
        {
            var vm = new DeepCleanupViewModel
            {
                SelectedLocation = new ScanLocation("Test", root),
                MinSizeMB = 1,
                TopCount = 10
            };
            var t = vm.ScanLargeFilesCommand.ExecuteAsync(null);
            if (t is Task tt) await tt;
            Assert.NotEmpty(vm.LargeFiles);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ScanLocation_RecordHoldsValues()
    {
        var loc = new ScanLocation("Downloads", @"C:\Users\X\Downloads");
        Assert.Equal("Downloads", loc.Label);
        Assert.Equal(@"C:\Users\X\Downloads", loc.Path);
    }

    [Fact]
    public void ScanLocation_Records_EquateByValue()
    {
        var a = new ScanLocation("A", "p");
        var b = new ScanLocation("A", "p");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1024)]
    [InlineData(10_000)]
    public void MinSizeMB_Settable(int v)
    {
        var vm = new DeepCleanupViewModel { MinSizeMB = v };
        Assert.Equal(v, vm.MinSizeMB);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    public void TopCount_Settable(int v)
    {
        var vm = new DeepCleanupViewModel { TopCount = v };
        Assert.Equal(v, vm.TopCount);
    }
}
