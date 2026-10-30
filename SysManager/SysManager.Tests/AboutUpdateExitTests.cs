// SysManager · AboutUpdateExitTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
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

    /// <summary>
    /// A SHA-256-shaped value, because the install hands the verified hash to the applier and
    /// <c>UpdateApplier.BuildArguments</c> refuses anything that is not 64 hex digits (#2832). "hash" used to do.
    /// </summary>
    private const string VerifiedHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    /// <summary>A view-model over the substitute update client, with the start and the shutdown recorded.</summary>
    /// <param name="verify">What the hash check answers; it passes unless a test says otherwise.</param>
    /// <param name="launch">What starting the new version does; it records the start unless a test says otherwise.</param>
    /// <param name="processPath">Where SysManager runs from; the test host unless a test says otherwise.</param>
    private AboutViewModel NewVm((bool, string?, string?)? verify = null, Action<ProcessStartInfo>? launch = null,
                                 Func<string?>? processPath = null)
    {
        _updates.GetLatestAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpdateService.ReleaseInfo?>(Release));
        _updates.VerifyHashAsync(Release, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool, string?, string?)>(verify ?? (true, VerifiedHash, VerifiedHash)));

        return new AboutViewModel(_updates, new SystemReportService(new SystemInfoService(), new DiskHealthService(), new BatteryService(), new EventLogService()),
            autoCheck: false, preferences: new UpdateCheckPreferenceService(_dir), updatesDir: _dir,
            launch: launch ?? _launched.Add, shutdown: () => _shutdowns++, processPath: processPath);
    }

    /// <summary>A view-model that has found the update and downloaded it, one click from Install.</summary>
    private async Task<AboutViewModel> ReadyToInstallAsync(
        (bool, string?, string?)? verify = null, Action<ProcessStartInfo>? launch = null, Func<string?>? processPath = null)
    {
        var vm = NewVm(verify, launch, processPath);
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

        // The verified hash goes with the launch, because the applier re-checks the bytes it installs against
        // it on a handle of its own — this one is closed by the time it reads (#2832).
        Assert.EndsWith(VerifiedHash, started.Arguments, StringComparison.Ordinal);
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
        KeptCopy.Keep(_dir);
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

    // ── A check that finds another release lets go of the old one's download (#2728) ──────────────
    //
    // The card named the newer release while still holding the older one's file. Install checked that file against
    // the newer release's hash and reported a mismatch every time, while Download and Manual download stayed hidden.

    private static readonly UpdateService.ReleaseInfo Newer =
        Release with { Version = new Version(99, 0, 1), Tag = "v99.0.1", Name = "SysManager 99.0.1" };

    private void GitHubNowAnswers(UpdateService.ReleaseInfo release) =>
        _updates.GetLatestAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<UpdateService.ReleaseInfo?>(release));

    [Fact]
    public async Task ACheckThatFindsANewerRelease_LetsGoOfTheOldDownload_AndOffersDownloadAgain()
    {
        var vm = await ReadyToInstallAsync();
        GitHubNowAnswers(Newer);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("v99.0.1", vm.LatestVersionLabel);
        Assert.Null(vm.DownloadedPath);
        Assert.True(vm.ShowDownloadButton);
        Assert.False(vm.AutoDownloadFailed);
        Assert.Equal("", vm.DownloadStatus);
    }

    [Fact]
    public async Task ACheckThatFindsANewerRelease_ClearsAFailedDownloadOfTheOldOne()
    {
        var vm = NewVm();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        _updates.DownloadAsync(Release, Arg.Any<IProgress<(long, long?)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.AutoDownloadFailed);   // the premise: the old release's download failed
        GitHubNowAnswers(Newer);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.False(vm.AutoDownloadFailed);
        Assert.Equal("", vm.DownloadStatus);
        Assert.True(vm.ShowDownloadButton);
    }

    [Fact]
    public async Task ACheckThatFindsTheSameRelease_KeepsTheDownload()
    {
        var vm = await ReadyToInstallAsync();
        var file = vm.DownloadedPath;
        GitHubNowAnswers(Release with { Body = "Notes edited after publishing." });

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(file, vm.DownloadedPath);
        Assert.False(vm.ShowDownloadButton);
    }

    [Fact]
    public async Task ADownloadThatEndsAfterANewerReleaseWasFound_IsNotOfferedForInstall()
    {
        var vm = NewVm();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        var file = Path.Combine(_dir, "SysManager-v99.0.0.exe");
        await File.WriteAllBytesAsync(file, "not a signed PE, just bytes"u8.ToArray());
        var parked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _updates.DownloadAsync(Release, Arg.Any<IProgress<(long, long?)>>(), Arg.Any<CancellationToken>())
            .Returns(parked.Task);

        var download = vm.DownloadCommand.ExecuteAsync(null);
        GitHubNowAnswers(Newer);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        parked.SetResult(file);
        await download;

        Assert.Equal("v99.0.1", vm.LatestVersionLabel);
        Assert.Equal(AboutViewModel.NewerReleaseWhileDownloadingLine, vm.DownloadStatus);
        Assert.Null(vm.DownloadedPath);
        Assert.True(vm.ShowDownloadButton);
    }

    [Fact]
    public async Task ADownloadThatEndsAfterTheSameReleaseWasFoundAgain_IsOfferedForInstall()
    {
        // The same release with its notes edited since, as they are after publishing: still the file's release.
        var vm = NewVm();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        var file = Path.Combine(_dir, "SysManager-v99.0.0.exe");
        await File.WriteAllBytesAsync(file, "not a signed PE, just bytes"u8.ToArray());
        var parked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _updates.DownloadAsync(Release, Arg.Any<IProgress<(long, long?)>>(), Arg.Any<CancellationToken>())
            .Returns(parked.Task);

        var download = vm.DownloadCommand.ExecuteAsync(null);
        GitHubNowAnswers(Release with { Body = "Notes edited after publishing." });
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        parked.SetResult(file);
        await download;

        Assert.Equal(file, vm.DownloadedPath);
        Assert.StartsWith("Download complete.", vm.DownloadStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckThatFindsANewerReleaseWhileInstallChecksTheFile_StopsTheInstall()
    {
        // The check let the file go and the card names the newer release, so installing it would install a version
        // the card no longer offers, under the newer one's name.
        var vm = await ReadyToInstallAsync();
        var hashed = new TaskCompletionSource<(bool, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _updates.VerifyHashAsync(Release, Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(hashed.Task);
        using var dialog = new DialogAnswer(confirm: true);

        var install = vm.InstallUpdateCommand.ExecuteAsync(null);
        GitHubNowAnswers(Newer);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        hashed.SetResult((true, VerifiedHash, VerifiedHash));
        await install;

        Assert.Equal(0, dialog.Calls);
        Assert.Empty(_launched);
        Assert.Equal(0, _shutdowns);
        Assert.Equal(AboutViewModel.NewerReleaseBeforeInstallLine, vm.DownloadStatus);
        Assert.Null(vm.DownloadedPath);
        Assert.False(vm.InstallFailed);
        Assert.True(vm.ShowDownloadButton);
    }

    // ── Refused before SysManager closes when it cannot replace itself (#2725) ───────────────────
    //
    // The applier refuses a copy under Program Files, and it decides only after SysManager has closed: Install and
    // Go back confirmed, closed SysManager, and nothing reopened.

    private static string? InProgramFiles() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SysManager", "SysManager.exe");

    [Fact]
    public async Task Install_FromAProgramFilesCopy_IsRefusedBeforeAnythingCloses_AndOffersManualDownload()
    {
        var vm = await ReadyToInstallAsync(processPath: InProgramFiles);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(AboutViewModel.CannotReplaceLine("it runs from a folder Windows protects, such as Program Files"),
            vm.DownloadStatus);
        Assert.Equal(0, dialog.Calls);
        Assert.Empty(_launched);
        Assert.Equal(0, _shutdowns);
        Assert.True(vm.AutoDownloadFailed);   // the warning row, and Manual download
        // The file is fine, only the place is not, so it is not deleted.
        _updates.DidNotReceiveWithAnyArgs().DiscardDownload(default!);
    }

    [Fact]
    public async Task GoingBack_FromAProgramFilesCopy_IsRefusedBeforeAnythingCloses()
    {
        KeptCopy.Keep(_dir);
        var vm = NewVm(processPath: InProgramFiles);
        Assert.True(vm.CanRollBack);   // the premise: a verifiable previous build is on offer
        using var dialog = new DialogAnswer(confirm: true);

        await vm.RollBackCommand.ExecuteAsync(null);

        Assert.Equal("SysManager can't go back here: it runs from a folder Windows protects, such as Program Files.",
            vm.RollBackStatus);
        Assert.Equal(0, dialog.Calls);
        Assert.Empty(_launched);
        Assert.Equal(0, _shutdowns);
    }

    // ── Go back names its version, and checks the saved copy off the UI thread (#2725) ─────────────────

    [Fact]
    public async Task GoingBack_AsksAboutTheVersionByName()
    {
        KeptCopy.Keep(_dir);
        var vm = NewVm();
        using var dialog = new DialogAnswer(confirm: false);

        await vm.RollBackCommand.ExecuteAsync(null);

        Assert.Contains("Go back to v1.0.0, the version you had before the last update?", Assert.Single(dialog.Messages),
            StringComparison.Ordinal);
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task GoingBack_SaysItIsCheckingTheSavedVersion_WhileItDoes()
    {
        KeptCopy.Keep(_dir);
        var vm = NewVm();
        var shown = new ConcurrentQueue<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AboutViewModel.RollBackStatus)) shown.Enqueue(vm.RollBackStatus);
        };
        using var dialog = new DialogAnswer(confirm: false);

        await vm.RollBackCommand.ExecuteAsync(null);

        Assert.Equal(new[] { AboutViewModel.CheckingSavedVersionLine, "" }, shown);
    }

    [Fact]
    public async Task GoingBack_AfterAnotherUpdateReplacedTheVersion_AsksNothing()
    {
        KeptCopy.Keep(_dir);
        var vm = NewVm();
        Assert.True(vm.CanRollBack);   // the premise: offered when the tab opened
        KeptCopy.Keep(_dir, installedBy: new Version(1, 0, 1));
        using var dialog = new DialogAnswer(confirm: true);

        await vm.RollBackCommand.ExecuteAsync(null);

        Assert.Equal(0, dialog.Calls);
        Assert.Empty(_launched);
        Assert.Equal(0, _shutdowns);
        Assert.False(vm.CanRollBack);
        Assert.Equal("The previous version is no longer available.", vm.RollBackStatus);
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
