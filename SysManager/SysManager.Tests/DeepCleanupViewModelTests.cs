using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="DeepCleanupViewModel"/>.
/// Heavier scan/clean tests that hit the real filesystem live in IntegrationTests.
/// </summary>
public class DeepCleanupViewModelTests
{
    private static DeepCleanupViewModel NewVm() => new();

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_SetsDefaultScanSummary()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.ScanSummary));
        Assert.Contains("Scan", vm.ScanSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_CleanSummaryEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.CleanSummary);
    }

    [Fact]
    public void Constructor_LargeScanStatusEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.LargeScanStatus);
    }

    [Fact]
    public void Constructor_CategoriesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Categories);
        Assert.Empty(vm.Categories);
    }

    [Fact]
    public void Constructor_LargeFilesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.LargeFiles);
        Assert.Empty(vm.LargeFiles);
    }

    [Fact]
    public void Constructor_ScanLocationsCollection_Exists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ScanLocations);
    }

    [Fact]
    public void Constructor_MinSizeMB_DefaultsTo500()
    {
        var vm = NewVm();
        Assert.Equal(500, vm.MinSizeMB);
    }

    [Fact]
    public void Constructor_TopCount_DefaultsTo100()
    {
        var vm = NewVm();
        Assert.Equal(100, vm.TopCount);
    }

    // ---------- running flags ----------

    [Fact]
    public void IsScanning_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void IsCleaning_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsCleaning);
    }

    [Fact]
    public void IsLargeScanning_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsLargeScanning);
    }

    // ---------- progress defaults ----------

    [Fact]
    public void ScanProgress_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.ScanProgress);
    }

    [Fact]
    public void CleanProgress_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.CleanProgress);
    }

    [Fact]
    public void ScanStatusLine_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.ScanStatusLine);
    }

    [Fact]
    public void CleanStatusLine_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.CleanStatusLine);
    }

    [Fact]
    public void LargeFilesScanned_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.LargeFilesScanned);
    }

    [Fact]
    public void LargeBytesScanned_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.LargeBytesScanned);
    }

    [Fact]
    public void LargeCurrentFolder_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.LargeCurrentFolder);
    }

    // ---------- computed properties ----------

    [Fact]
    public void TotalSelectedBytes_ZeroWhenNoCategoriesSelected()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.TotalSelectedBytes);
    }

    [Fact]
    public void TotalSelectedDisplay_StartsWithZero()
    {
        var vm = NewVm();
        Assert.StartsWith("0", vm.TotalSelectedDisplay);
    }

    [Fact]
    public void LargeBytesScannedDisplay_DefaultsToZero()
    {
        var vm = NewVm();
        Assert.StartsWith("0", vm.LargeBytesScannedDisplay);
    }

    [Fact]
    public void LargeBytesScanned_Change_FiresDisplayPropertyChanged()
    {
        var vm = NewVm();
        var fired = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.LargeBytesScannedDisplay)) fired = true;
        };
        vm.LargeBytesScanned = 1024 * 1024;
        Assert.True(fired);
    }

    // ---------- TotalSelectedBytes with real categories ----------

    [Fact]
    public void TotalSelectedBytes_SumsSelectedCategories()
    {
        var vm = NewVm();
        var c1 = new CleanupCategory
        {
            Name = "A", Description = "a", Paths = Array.Empty<string>(),
            TotalSizeBytes = 1000, FileCount = 1, IsSelected = true
        };
        var c2 = new CleanupCategory
        {
            Name = "B", Description = "b", Paths = Array.Empty<string>(),
            TotalSizeBytes = 2000, FileCount = 2, IsSelected = false
        };
        vm.Categories.Add(c1);
        vm.Categories.Add(c2);
        Assert.Equal(1000, vm.TotalSelectedBytes);

        c2.IsSelected = true;
        Assert.Equal(3000, vm.TotalSelectedBytes);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("CleanCommand")]
    [InlineData("SelectAllCommand")]
    [InlineData("CancelCommand")]
    [InlineData("ScanLargeFilesCommand")]
    [InlineData("ShowInExplorerCommand")]
    [InlineData("CopyPathCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- cancel ----------

    [Fact]
    public void CancelCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCommand_RequestsCancellationOnLiveTokenSources()
    {
        var vm = NewVm();
        var scanCts = new CancellationTokenSource();
        var cleanCts = new CancellationTokenSource();
        var largeCts = new CancellationTokenSource();

        typeof(DeepCleanupViewModel)
            .GetField("_scanCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, scanCts);
        typeof(DeepCleanupViewModel)
            .GetField("_cleanCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, cleanCts);
        typeof(DeepCleanupViewModel)
            .GetField("_largeCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, largeCts);

        vm.CancelCommand.Execute(null);

        Assert.True(scanCts.IsCancellationRequested);
        Assert.True(cleanCts.IsCancellationRequested);
        Assert.True(largeCts.IsCancellationRequested);
    }

    // ---------- SelectAll ----------

    [Fact]
    public void SelectAll_True_SelectsNonDestructive_SkipsDestructive()
    {
        var vm = NewVm();
        vm.Categories.Add(new CleanupCategory
        {
            Name = "Safe", Description = "d", Paths = Array.Empty<string>(),
            TotalSizeBytes = 100, FileCount = 1, IsDestructiveHint = false
        });
        vm.Categories.Add(new CleanupCategory
        {
            Name = "Dangerous", Description = "d", Paths = Array.Empty<string>(),
            TotalSizeBytes = 200, FileCount = 1, IsDestructiveHint = true
        });

        vm.SelectAllCommand.Execute(true);

        Assert.True(vm.Categories[0].IsSelected);
        Assert.False(vm.Categories[1].IsSelected);
    }

    [Fact]
    public void SelectAll_False_DeselectsEverything()
    {
        var vm = NewVm();
        var c = new CleanupCategory
        {
            Name = "X", Description = "d", Paths = Array.Empty<string>(),
            TotalSizeBytes = 100, FileCount = 1, IsSelected = true
        };
        vm.Categories.Add(c);

        vm.SelectAllCommand.Execute(false);

        Assert.False(c.IsSelected);
    }

    [Fact]
    public void SelectAll_Null_TreatedAsTrue()
    {
        var vm = NewVm();
        vm.Categories.Add(new CleanupCategory
        {
            Name = "X", Description = "d", Paths = Array.Empty<string>(),
            TotalSizeBytes = 100, FileCount = 1, IsDestructiveHint = false
        });

        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.Categories[0].IsSelected);
    }

    // ---------- guard conditions ----------

    [Fact]
    public async Task Scan_WhenAlreadyScanning_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.IsScanning = true;
        var before = vm.ScanSummary;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(before, vm.ScanSummary);
    }

    [Fact]
    public async Task Clean_WhenAlreadyCleaning_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.IsCleaning = true;
        vm.CleanSummary = "marker";

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.CleanSummary);
    }

    [Fact]
    public async Task Clean_WhenNothingSelected_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.CleanSummary = "marker";

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.CleanSummary);
    }

    [Fact]
    public async Task ScanLargeFiles_WhenAlreadyScanning_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.IsLargeScanning = true;
        vm.LargeScanStatus = "marker";

        await vm.ScanLargeFilesCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.LargeScanStatus);
    }

    [Fact]
    public async Task ScanLargeFiles_NoLocation_SetsErrorStatus()
    {
        var vm = NewVm();
        vm.SelectedLocation = null;

        await vm.ScanLargeFilesCommand.ExecuteAsync(null);

        Assert.Contains("location", vm.LargeScanStatus, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- ShowInExplorer / CopyPath safe calls ----------

    [Fact]
    public void ShowInExplorer_NullPath_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void ShowInExplorer_EmptyPath_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(""));
        Assert.Null(ex);
    }

    [Fact]
    public void ShowInExplorer_NonExistentPath_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(@"C:\no_such_" + Guid.NewGuid().ToString("N")));
        Assert.Null(ex);
    }

    [Fact]
    public void CopyPath_NullPath_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CopyPathCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CopyPath_EmptyPath_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CopyPathCommand.Execute(""));
        Assert.Null(ex);
    }

    // ---------- setters ----------

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1024)]
    [InlineData(10_000)]
    public void MinSizeMB_Settable(int v)
    {
        var vm = NewVm();
        vm.MinSizeMB = v;
        Assert.Equal(v, vm.MinSizeMB);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    public void TopCount_Settable(int v)
    {
        var vm = NewVm();
        vm.TopCount = v;
        Assert.Equal(v, vm.TopCount);
    }

    // ---------- ScanLocation record ----------

    [Fact]
    public void ScanLocation_HoldsValues()
    {
        var loc = new ScanLocation("Downloads", @"C:\Users\X\Downloads");
        Assert.Equal("Downloads", loc.Label);
        Assert.Equal(@"C:\Users\X\Downloads", loc.Path);
    }

    [Fact]
    public void ScanLocation_ValueEquality()
    {
        var a = new ScanLocation("A", "p");
        var b = new ScanLocation("A", "p");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ScanLocation_DifferentValues_NotEqual()
    {
        var a = new ScanLocation("A", "p1");
        var b = new ScanLocation("A", "p2");
        Assert.NotEqual(a, b);
    }
}
