// SysManager · AboutUpdateExitTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Installing an update and going back to the previous version both close SysManager, and each asks first,
/// naming anything still running (#2499).
/// </summary>
/// <remarks>
/// Install asked nothing at all: it started the new version and shut down half a second later. The build that
/// would be started and the shutdown are both injected, so nothing is launched or closed here. The update file is
/// a few plain bytes, which the Authenticode check treats as an unsigned build, and the release comes from a
/// substitute whose hash check passes, so the command reaches its question exactly as a real verified download
/// does.
/// </remarks>
// Serialized: these swap DialogService.Instance and hold OperationLockService locks, both process-wide.
[Collection("ProcessWideStatics")]
public sealed class AboutUpdateExitTests : IDisposable
{
    private static readonly UpdateService.ReleaseInfo Release = new(
        new Version(99, 0, 0), "v99.0.0", "SysManager 99.0.0", "Notes.", DateTimeOffset.UnixEpoch,
        "https://github.com/laurentiu021/SystemManager/releases/tag/v99.0.0", AssetUrl: null, AssetSize: null);

    private readonly string _dir;
    private readonly List<ProcessStartInfo> _launched = [];
    private int _shutdowns;

    public AboutUpdateExitTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerAboutExitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private AboutViewModel NewVm()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.GetLatestAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpdateService.ReleaseInfo?>(Release));
        updates.VerifyHashAsync(Release, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool, string?, string?)>((true, "hash", "hash")));

        return new AboutViewModel(updates, new SystemReportService(new SystemInfoService(), new DiskHealthService()),
            autoCheck: false, preferences: new UpdateCheckPreferenceService(_dir), updatesDir: _dir,
            launch: _launched.Add, shutdown: () => _shutdowns++);
    }

    /// <summary>A view-model that has found the update and downloaded it, one click from Install.</summary>
    private async Task<AboutViewModel> ReadyToInstallAsync()
    {
        var vm = NewVm();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal("v99.0.0", vm.LatestVersionLabel);   // the premise: the update was found

        var file = Path.Combine(_dir, "SysManager-v99.0.0.exe");
        await File.WriteAllBytesAsync(file, "not a signed PE, just bytes"u8.ToArray());
        vm.DownloadedPath = file;
        return vm;
    }

    [Fact]
    public async Task Install_AsksFirst_AndDecliningStartsAndClosesNothing()
    {
        var vm = await ReadyToInstallAsync();
        using var dialog = new DialogAnswer(confirm: false);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialog.Messages);
        Assert.StartsWith("Install update\n", shown);
        Assert.Contains("Install SysManager v99.0.0?", shown);
        Assert.Contains("SysManager closes", shown);
        Assert.Empty(_launched);
        Assert.Equal(0, _shutdowns);
        Assert.Contains("ready to install", vm.DownloadStatus);
    }

    [Fact]
    public async Task Install_Confirmed_StartsTheVerifiedFileAndThenCloses()
    {
        var vm = await ReadyToInstallAsync();
        using var dialog = new DialogAnswer(confirm: true);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        var started = Assert.Single(_launched);
        Assert.Equal(vm.DownloadedPath, started.FileName);
        Assert.Equal(1, _shutdowns);
    }

    [Fact]
    public async Task Install_WhileSomethingRuns_NamesItInTheQuestion()
    {
        var vm = await ReadyToInstallAsync();
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        Assert.NotNull(held);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialog.Messages);
        Assert.Contains("still working on: SFC scan.", shown);
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task GoingBack_WhileSomethingRuns_NamesItInTheQuestion()
    {
        var previous = Path.Combine(_dir, UpdateApplier.PreviousBuildFileName);
        await File.WriteAllTextAsync(previous, "OLD-BUILD");
        await File.WriteAllTextAsync(UpdateApplier.PreviousBuildHashPath(_dir), UpdateApplier.ComputeFileHash(previous));
        var vm = NewVm();
        Assert.True(vm.CanRollBack);   // the premise: a verifiable previous build is on offer
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup");
        Assert.NotNull(held);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.RollBackCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialog.Messages);
        Assert.StartsWith("Go back to the previous version\n", shown);
        Assert.Contains("still working on: Deep Cleanup.", shown);
        Assert.Empty(_launched);
        Assert.Equal(0, _shutdowns);
    }
}
