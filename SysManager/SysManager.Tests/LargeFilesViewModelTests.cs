// SysManager · LargeFilesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
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
    private static LargeFilesViewModel NewVm() => NewVm(out _);

    // Each Explorer launch is recorded instead of made, so a broken check in front of it fails a test rather than
    // opening a window on the PC running the suite (#2740).
    private static LargeFilesViewModel NewVm(out List<string> launches)
    {
        var opened = new List<string>();
        launches = opened;
        return new(new Services.LargeFileScanner(), new Services.FixedDriveService(), opened.Add);
    }

    [Fact]
    public void AScan_ShowsOnTheTaskbarButton_ForAsLongAsItRuns()
    {
        // The tab's bar is a marquee with no figure, and the scan reached only IsBusy, so the taskbar button, the one
        // sign of progress once the window is minimised, stayed blank for the whole scan (#2872).
        var vm = NewVm();

        vm.IsScanning = true;
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsProgressIndeterminate);

        vm.IsScanning = false;
        Assert.False(vm.IsProgressIndeterminate);
    }

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

    // ── The result names what was read, and a location that is gone is said to be (#2764) ──

    /// <summary>A scan whose result is held until the test lets it finish, and what it was asked to read.</summary>
    private sealed class HeldScan
    {
        private readonly TaskCompletionSource<IReadOnlyList<LargeFileEntry>> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Root, long MinBytes)> Asked { get; } = [];

        public Task<IReadOnlyList<LargeFileEntry>> ScanAsync(string root, long minBytes, int top,
            IProgress<Services.LargeFileScanner.LargeFileProgress>? progress, CancellationToken ct)
        {
            lock (Asked) Asked.Add((root, minBytes));
            // Bounded, so a scan nothing lets finish fails the test instead of hanging the suite.
            return _result.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }

        public void Finish() => _result.TrySetResult([]);
    }

    /// <summary>
    /// The result line names the location and size the scan read, though both were changed while it ran (#2764).
    /// </summary>
    /// <remarks>
    /// The scan started with the selection, and the line, the toast and the log were built from it again once the scan
    /// had ended, while the box and the size stayed open to change. Picking Documents during a scan of Downloads
    /// reported Downloads' files as Documents'.
    /// </remarks>
    [Fact]
    public async Task TheResultLine_NamesTheLocationAndSizeTheScanRead_WhenTheyChangedWhileItRan()
    {
        var held = new HeldScan();
        var vm = new LargeFilesViewModel(new Services.LargeFileScanner(), new Services.FixedDriveService(), _ => { },
            held.ScanAsync);
        await vm.InitializationComplete;
        Assert.True(vm.ScanLocations.Count >= 2, $"only {vm.ScanLocations.Count} locations here, and this needs two");
        var read = vm.ScanLocations[0];
        vm.SelectedLocation = read;
        vm.MinSizeMB = 500;

        var scan = vm.ScanCommand.ExecuteAsync(null);
        vm.SelectedLocation = vm.ScanLocations[1];
        vm.MinSizeMB = 100;
        held.Finish();
        await scan;

        Assert.Equal((read.Path, 500L * 1024 * 1024), Assert.Single(held.Asked));
        Assert.Equal($"Found 0 files ≥ 500 MB in {read.Label.Trim()}.", vm.ScanStatus);
    }

    /// <summary>A folder that is gone is not scanned as one with nothing in it (#2764).</summary>
    [Fact]
    public async Task AScanOfAFolderThatIsGone_SaysSo_AndReadsNothing()
    {
        var held = new HeldScan();
        held.Finish();
        var vm = new LargeFilesViewModel(new Services.LargeFileScanner(), new Services.FixedDriveService(), _ => { },
            held.ScanAsync);
        await vm.InitializationComplete;
        var gone = new ScanLocation("📥  Downloads",
            Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N")));
        vm.SelectedLocation = gone;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Empty(held.Asked);
        Assert.Equal("📥  Downloads is no longer there, so nothing was scanned.", vm.ScanStatus);
    }

    /// <summary>
    /// A drive unplugged since the list was read is said to be no longer connected, and the list is read again (#2764).
    /// </summary>
    /// <remarks>
    /// The scanner returns nothing for a folder that is not there, so the tab said "Found 0 files ≥ 500 MB in Whole drive
    /// E:" with a "Large file scan complete" toast: a wrong answer to what is filling that disk.
    /// </remarks>
    [Fact]
    public async Task AScanOfADriveThatIsGone_SaysItIsNoLongerConnected_AndReadsTheListAgain()
    {
        var free = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").FirstOrDefault(root => !Directory.Exists(root));
        if (free is null) Assert.Skip("every drive letter from D: to Z: is in use on this PC");
        var held = new HeldScan();
        held.Finish();
        var reads = 0;
        var vm = new LargeFilesViewModel(new Services.LargeFileScanner(), new Services.FixedDriveService(), _ => { },
            held.ScanAsync, () =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<IReadOnlyList<Services.FixedDriveService.FixedDrive>>([]);
            });
        await vm.InitializationComplete;
        vm.SelectedLocation = new ScanLocation($"💾  Whole drive  {free[..2]}  (931 GB)", free);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Empty(held.Asked);
        Assert.Equal($"Drive {free[..2]} is no longer connected, so nothing was scanned.", vm.ScanStatus);
        Assert.Equal(2, Volatile.Read(ref reads));
    }

    /// <summary>
    /// Showing the tab reads the locations again, so a drive connected since it opened is listed, and the location
    /// picked stays picked (#2764).
    /// </summary>
    /// <remarks>
    /// The list was read once, when the tab was built, and the tab is kept for the whole run: a drive connected later
    /// never appeared, and one unplugged stayed listed with its old size.
    /// </remarks>
    [Fact]
    public async Task ShowingTheTab_ReadsTheLocationsAgain_AndKeepsTheOnePicked()
    {
        var reads = 0;
        var vm = new LargeFilesViewModel(new Services.LargeFileScanner(), new Services.FixedDriveService(), _ => { },
            enumerateDrives: () => Task.FromResult<IReadOnlyList<Services.FixedDriveService.FixedDrive>>(
                Interlocked.Increment(ref reads) == 1
                    ? []
                    : [new Services.FixedDriveService.FixedDrive("C:", "", "NTFS", 931, 100, "SSD", "NVMe")]));
        await vm.InitializationComplete;
        Assert.DoesNotContain(vm.ScanLocations, l => l.Path == @"C:\");
        var picked = vm.ScanLocations[^1];
        vm.SelectedLocation = picked;

        vm.IsActive = true;
        await vm.ShownLocations;

        Assert.Contains(vm.ScanLocations, l => l.Path == @"C:\");
        Assert.Equal(picked.Path, vm.SelectedLocation?.Path);
    }

    [Fact]
    public void ShowInExplorer_NullPath_DoesNotThrow()
    {
        var vm = NewVm(out var launches);
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(null));
        Assert.Null(ex);
        Assert.Empty(launches);
    }

    [Fact]
    public void ShowInExplorer_EmptyPath_DoesNotThrow()
    {
        var vm = NewVm(out var launches);
        var ex = Record.Exception(() => vm.ShowInExplorerCommand.Execute(""));
        Assert.Null(ex);
        Assert.Empty(launches);
    }

    [Fact]
    public void ShowInExplorer_AFileThatExists_OpensExplorerOnIt()
    {
        var file = Path.Combine(Path.GetTempPath(), "smtest_largefile_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(file, [1]);
        try
        {
            var vm = NewVm(out var launches);

            vm.ShowInExplorerCommand.Execute(file);

            Assert.Equal([file], launches);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ShowInExplorer_NonExistentPath_SaysFileNotFound()
    {
        // The scan is a point in time; the file can be gone by the time the user acts on the row. This
        // used to return silently, leaving no way to tell "nothing happened" from "it opened behind
        // another window" (#2859).
        var vm = NewVm(out var launches);
        vm.ShowInExplorerCommand.Execute(@"C:\no_such_" + Guid.NewGuid().ToString("N"));

        Assert.Contains("not found", vm.ScanStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(launches);
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

    [Fact]
    public void CopyPath_RealPath_SetsAStatusMessage()
    {
        // The defect: Copy path swallowed a busy clipboard with no message at all, so nothing told the
        // user whether anything happened (#2859). Whether the sandbox's clipboard is actually available
        // is not under this test's control, so it asserts only that SOME outcome was reported.
        var vm = NewVm();
        vm.CopyPathCommand.Execute(@"C:\Windows\System32\notepad.exe");

        Assert.False(string.IsNullOrWhiteSpace(vm.ScanStatus));
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
