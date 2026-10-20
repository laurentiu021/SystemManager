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

    private readonly IUpdateService _updates = Substitute.For<IUpdateService>();

    /// <summary>A view-model over the substitute update client, with the start and the shutdown recorded.</summary>
    /// <param name="verify">What the hash check answers; it passes unless a test says otherwise.</param>
    /// <param name="launch">What starting the new version does; it records the start unless a test says otherwise.</param>
    private AboutViewModel NewVm((bool, string?, string?)? verify = null, Action<ProcessStartInfo>? launch = null)
    {
        _updates.GetLatestAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpdateService.ReleaseInfo?>(Release));
        _updates.VerifyHashAsync(Release, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool, string?, string?)>(verify ?? (true, "hash", "hash")));

        return new AboutViewModel(_updates, new SystemReportService(new SystemInfoService(), new DiskHealthService(), new BatteryService(), new EventLogService()),
            autoCheck: false, preferences: new UpdateCheckPreferenceService(_dir), updatesDir: _dir,
            launch: launch ?? _launched.Add, shutdown: () => _shutdowns++);
    }

    /// <summary>A view-model that has found the update and downloaded it, one click from Install.</summary>
    private async Task<AboutViewModel> ReadyToInstallAsync(
        (bool, string?, string?)? verify = null, Action<ProcessStartInfo>? launch = null)
    {
        var vm = NewVm(verify, launch);
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

    // ── Install's refusals and failures read as problems ────────────────────────
    //
    // Install wrote every one of them to DownloadStatus, which with a downloaded file only the green "download
    // complete" row showed, so "SHA256 mismatch" read as success, with Install offered beside it.

    [Fact]
    public async Task AFileWhoseHashDoesNotMatch_IsLetGo_AndDownloadComesBack()
    {
        var vm = await ReadyToInstallAsync(verify: (false, new string('A', 64), new string('B', 64)));
        var file = vm.DownloadedPath!;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.StartsWith("SHA256 mismatch", vm.DownloadStatus, StringComparison.Ordinal);
        Assert.True(vm.AutoDownloadFailed);   // the warning row, and Manual download
        Assert.Null(vm.DownloadedPath);       // no green row, and no Install
        Assert.True(vm.ShowDownloadButton);
        // Deleted, so the next Download fetches it again: the cache would hand the same file back.
        _updates.Received(1).DiscardDownload(file);
        Assert.Empty(_launched);
        Assert.Equal(0, dialog.Calls);
    }

    [Fact]
    public async Task AHashThatCouldNotBeChecked_IsAProblem_AndInstallStaysOffered()
    {
        // The published hash was not fetched, which says nothing against the file, so it is kept.
        var vm = await ReadyToInstallAsync(verify: (false, null, null));
        var file = vm.DownloadedPath;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.InstallFailed);
        Assert.Contains("Could not check the download", vm.DownloadStatus, StringComparison.Ordinal);
        Assert.Equal(file, vm.DownloadedPath);
        Assert.False(vm.AutoDownloadFailed);
        _updates.DidNotReceiveWithAnyArgs().DiscardDownload(default!);
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task ANewVersionThatWouldNotStart_IsAProblem_AndNothingCloses()
    {
        var vm = await ReadyToInstallAsync(launch: _ => throw new System.ComponentModel.Win32Exception(1223, "The operation was canceled by the user."));
        var file = vm.DownloadedPath;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.InstallFailed);
        Assert.Equal("Update failed: The operation was canceled by the user.", vm.DownloadStatus);
        Assert.Equal(file, vm.DownloadedPath);   // Install is offered again
        Assert.Equal(0, _shutdowns);
        _updates.DidNotReceiveWithAnyArgs().DiscardDownload(default!);
    }

    [Fact]
    public async Task ANewDownload_ClearsAnInstallProblem()
    {
        var vm = await ReadyToInstallAsync(verify: (false, null, null));
        using var dialog = new DialogAnswer(confirm: false);
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.True(vm.InstallFailed);   // the premise
        _updates.DownloadAsync(Release, Arg.Any<IProgress<(long, long?)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(vm.DownloadedPath));

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.False(vm.InstallFailed);
        Assert.StartsWith("Download complete.", vm.DownloadStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatIsGone_IsLetGo_AndDownloadComesBack()
    {
        var vm = await ReadyToInstallAsync();
        File.Delete(vm.DownloadedPath!);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal("No downloaded file to install.", vm.DownloadStatus);
        Assert.True(vm.AutoDownloadFailed);
        Assert.Null(vm.DownloadedPath);
        Assert.False(vm.InstallFailed);
    }
}
