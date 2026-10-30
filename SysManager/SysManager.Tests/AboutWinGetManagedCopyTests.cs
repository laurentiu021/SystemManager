// SysManager · AboutWinGetManagedCopyTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// A copy winget's portable installer manages must never have About replace its own exe: doing so left
/// winget's own record of the installed version stale, and the next <c>winget upgrade</c> or
/// <c>winget uninstall</c> of that package then refused with "the package has been modified" (#2889).
/// </summary>
/// <remarks>
/// Reuses <see cref="AboutUpdateExitTests"/>'s fixture shape (a substituted <see cref="IUpdateService"/>,
/// recorded launch/shutdown) with one more seam threaded: <c>processPath</c>, which decides
/// <see cref="AboutViewModel.IsManagedByWinGet"/>.
/// </remarks>
[Collection("ProcessWideStatics")]
public sealed class AboutWinGetManagedCopyTests : IDisposable
{
    // AboutViewModel.IsManagedByWinGet reads UpdateService.IsRunningFromWinGet(processPath) — the one-arg
    // overload, which checks THIS machine's real per-user and per-machine special folders. A fixed fake
    // "C:\Users\test\..." path is never inside the real %LOCALAPPDATA% on whatever machine runs the test,
    // so the fake path here is built under the REAL local app data root instead.
    private static readonly string WinGetExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WinGet", "Packages",
        "laurentiu021.SysManager_Microsoft.Winget.Source_8wekyb3d8bbwe", "SysManager.exe");

    private static readonly string OrdinaryExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "SysManager", "SysManager.exe");

    private static readonly UpdateService.ReleaseInfo Release = new(
        new Version(99, 0, 0), "v99.0.0", "SysManager 99.0.0", "Notes.", DateTimeOffset.UnixEpoch,
        "https://github.com/laurentiu021/SystemManager/releases/tag/v99.0.0", AssetUrl: null, AssetSize: null);

    private readonly string _dir;
    private readonly List<ProcessStartInfo> _launched = [];
    private int _shutdowns;
    private readonly IUpdateService _updates = Substitute.For<IUpdateService>();

    public AboutWinGetManagedCopyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerAboutWinGetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private AboutViewModel NewVm(string processPath)
    {
        _updates.GetLatestAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpdateService.ReleaseInfo?>(Release));

        return new AboutViewModel(_updates,
            new SystemReportService(new SystemInfoService(), new DiskHealthService(), new BatteryService(), new EventLogService()),
            autoCheck: false, preferences: new UpdateCheckPreferenceService(_dir), updatesDir: _dir,
            launch: _launched.Add, shutdown: () => _shutdowns++, processPath: () => processPath);
    }

    [Fact]
    public void Constructor_WithAWinGetPackagesPath_SetsIsManagedByWinGet()
        => Assert.True(NewVm(WinGetExePath).IsManagedByWinGet);

    [Fact]
    public void Constructor_WithAnOrdinaryPath_LeavesIsManagedByWinGetFalse()
        => Assert.False(NewVm(OrdinaryExePath).IsManagedByWinGet);

    [Fact]
    public void ShowDownloadButton_ForAWinGetManagedCopy_IsFalseEvenBeforeAnyDownload()
        => Assert.False(NewVm(WinGetExePath).ShowDownloadButton);

    [Fact]
    public void ShowDownloadButton_ForAnOrdinaryCopy_IsStillTrueBeforeAnyDownload()
        => Assert.True(NewVm(OrdinaryExePath).ShowDownloadButton);

    [Fact]
    public async Task CheckForUpdates_OnAWinGetManagedCopy_PointsAtWinGetUpgradeRatherThanDownload()
    {
        var vm = NewVm(WinGetExePath);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(vm.UpdateAvailable);
        Assert.Contains("winget upgrade laurentiu021.SysManager", vm.UpdateStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Click Download", vm.UpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdates_OnAnOrdinaryCopy_StillPointsAtDownload()
    {
        var vm = NewVm(OrdinaryExePath);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("Click Download", vm.UpdateStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("winget upgrade", vm.UpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_OnAWinGetManagedCopy_IsANoOp()
    {
        var vm = NewVm(WinGetExePath);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.UpdateAvailable); // the premise: an update exists to (not) download

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Null(vm.DownloadedPath);
        Assert.False(vm.IsDownloading);
        Assert.Empty(_launched);
        await _updates.DidNotReceiveWithAnyArgs()
            .DownloadAsync(default!, default!, default);
    }

    [Fact]
    public async Task InstallUpdateAsync_OnAWinGetManagedCopy_RefusesAndLaunchesNothing()
    {
        var vm = NewVm(WinGetExePath);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Contains("winget upgrade laurentiu021.SysManager", vm.DownloadStatus, StringComparison.Ordinal);
        Assert.True(vm.InstallFailed);
        Assert.Empty(_launched);
        Assert.Equal(0, _shutdowns);
    }

    [Fact]
    public async Task RollBackAsync_OnAWinGetManagedCopy_IsNeverOfferedAndRefusesIfCalledDirectly()
    {
        // A previous build retained on disk — the premise CanRollBack would otherwise be true for.
        var previous = Path.Combine(_dir, UpdateApplier.PreviousBuildFileName);
        await File.WriteAllTextAsync(previous, "OLD-BUILD");
        await File.WriteAllTextAsync(UpdateApplier.PreviousBuildHashPath(_dir), UpdateApplier.ComputeFileHash(previous));

        var vm = NewVm(WinGetExePath);
        Assert.False(vm.CanRollBack); // never offered for a winget-managed copy, files or not

        await vm.RollBackCommand.ExecuteAsync(null);

        Assert.Contains("managed by winget", vm.RollBackStatus, StringComparison.Ordinal);
        Assert.Empty(_launched);
        Assert.Equal(0, _shutdowns);
    }

    [Fact]
    public async Task RollBackAsync_OnAnOrdinaryCopyWithTheSameFiles_IsStillOffered()
    {
        var previous = Path.Combine(_dir, UpdateApplier.PreviousBuildFileName);
        await File.WriteAllTextAsync(previous, "OLD-BUILD");
        await File.WriteAllTextAsync(UpdateApplier.PreviousBuildHashPath(_dir), UpdateApplier.ComputeFileHash(previous));

        var vm = NewVm(OrdinaryExePath);

        Assert.True(vm.CanRollBack);
    }
}
