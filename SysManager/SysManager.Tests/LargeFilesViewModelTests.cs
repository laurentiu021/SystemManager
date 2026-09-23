// SysManager · LargeFilesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="LargeFilesViewModel"/>. Moved here with the feature when it left Deep Cleanup
/// (#1523); the assertions are the same ones, against the renamed surface.
/// </summary>
/// <remarks>
/// Serialized because the scan takes the process-wide <see cref="Services.OperationLockService"/> Disk lock.
/// </remarks>
[Collection("ProcessWideStatics")]
public class LargeFilesViewModelTests
{
    private static LargeFilesViewModel NewVm() =>
        new(new Services.LargeFileScanner(), new Services.FixedDriveService());

    [Fact]
    public void Constructor_ScanStatusEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.ScanStatus);
    }

    [Fact]
    public void Constructor_FilesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Files);
        Assert.Empty(vm.Files);
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

    [Fact]
    public void IsScanning_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void FilesScanned_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.FilesScanned);
    }

    [Fact]
    public void BytesScanned_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.BytesScanned);
    }

    [Fact]
    public void CurrentFolder_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.CurrentFolder);
    }

    [Fact]
    public void BytesScannedDisplay_DefaultsToZero()
    {
        var vm = NewVm();
        Assert.StartsWith("0", vm.BytesScannedDisplay);
    }

    [Fact]
    public void BytesScanned_Change_FiresDisplayPropertyChanged()
    {
        var vm = NewVm();
        var fired = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.BytesScannedDisplay)) fired = true;
        };
        vm.BytesScanned = 1024 * 1024;
        Assert.True(fired);
    }

    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("CancelCommand")]
    [InlineData("ShowInExplorerCommand")]
    [InlineData("CopyPathCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    [Fact]
    public async Task Scan_WhenAlreadyScanning_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.IsScanning = true;
        vm.ScanStatus = "marker";

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.ScanStatus);
    }

    /// <summary>
    /// The constructor's background load picks a default location — which is why the test below awaits it.
    /// </summary>
    /// <remarks>
    /// This is the mechanism behind the CI failure, observed directly rather than inferred from a timing
    /// race. Clearing <c>SelectedLocation</c> and then letting the load finish shows the value coming back:
    /// the load ends with <c>SelectedLocation = ScanLocations.FirstOrDefault()</c> and does not check
    /// whether anything set it in the meantime. Any test that clears the property without first awaiting
    /// initialization is racing that assignment, and on a slower machine it loses.
    /// <para>Picking a default is deliberate product behaviour — the tab opens ready to scan rather than
    /// demanding a choice first — so this pins it rather than treating it as the bug. The bug is only ever
    /// a test that does not account for it.</para>
    /// </remarks>
    [Fact]
    public async Task TheConstructorLoad_PicksADefaultLocation_EvenAfterOneWasCleared()
    {
        var vm = NewVm();
        vm.SelectedLocation = null;

        await vm.InitializationComplete;

        // Asserted rather than returned on. The load cannot legitimately produce an empty list: the six
        // known folders fall back instead of throwing, Program Files is added before the only call that can
        // throw at all, and AddLocation swallows nothing but a missing directory. An empty list therefore
        // means the enumeration failed — which returning here reported as a pass.
        Assert.NotEmpty(vm.ScanLocations);

        Assert.NotNull(vm.SelectedLocation);
        Assert.Same(vm.ScanLocations[0], vm.SelectedLocation);
    }

    /// <summary>
    /// With no location picked, the scan says so instead of scanning something the user did not choose.
    /// </summary>
    /// <remarks>
    /// <b>Awaiting <c>InitializationComplete</c> is what makes this deterministic, and it is not
    /// decoration.</b> The constructor launches its location enumeration fire-and-forget, and that load
    /// ends by assigning <c>SelectedLocation = ScanLocations.FirstOrDefault()</c>. Setting the property to
    /// null before the load finishes therefore gets overwritten mid-test, and the scan runs against
    /// Downloads — the assertion then fails with "Found 0 files ≥ 500 MB in Downloads", which reads like a
    /// broken feature rather than a race.
    /// <para>It went red in CI on the pull request that added an unrelated test class, because that shifted
    /// the timing enough to lose a race this machine happened to win. <c>ViewModelBase</c> exposes
    /// <c>InitializationComplete</c> for exactly this ("tests can await it to observe the loaded state
    /// deterministically instead of racing the fire-and-forget load"), so the seam existed and this test
    /// simply was not using it. The wider sweep of tests with the same exposure is #2201.</para>
    /// </remarks>
    [Fact]
    public async Task Scan_NoLocation_SetsErrorStatus()
    {
        var vm = NewVm();
        await vm.InitializationComplete;

        vm.SelectedLocation = null;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains("location", vm.ScanStatus, StringComparison.OrdinalIgnoreCase);
    }

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

    [Fact]
    public void IsScanning_True_SetsIsBusy()
    {
        var vm = NewVm();
        vm.IsScanning = true;
        Assert.True(vm.IsBusy);
    }
}
