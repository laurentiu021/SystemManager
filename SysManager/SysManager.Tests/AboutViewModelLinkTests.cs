// SysManager · AboutViewModelLinkTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Each link on the About tab opens the address it names, in the browser (#2742).
/// </summary>
/// <remarks>
/// The links went through a static <c>OpenUrl</c> that returned at once when there was no WPF application, so the
/// tests of three of them ran nothing and checked only that nothing threw, and a wrong address would have passed,
/// including the latest-release page Manual download falls back on when the automatic download fails. They now open
/// through the launcher the tab already takes for its installer, recorded here. The tab's own launcher still starts
/// nothing without a WPF application, so a test that leaves it out cannot open a browser.
/// </remarks>
// Serialized: the bug report's link reads elevation, which ForceElevation pins for the whole process.
[Collection("ProcessWideStatics")]
public sealed class AboutViewModelLinkTests : IDisposable
{
    private const string Repository = "https://github.com/laurentiu021/SystemManager";

    private static readonly UpdateService.ReleaseInfo Release = new(
        new Version(99, 0, 0), "v99.0.0", "SysManager 99.0.0", "Notes.", DateTimeOffset.UnixEpoch,
        Repository + "/releases/tag/v99.0.0", AssetUrl: null, AssetSize: null);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerAboutLinkTests", Guid.NewGuid().ToString("N"));
    private readonly List<ProcessStartInfo> _launched = [];
    private readonly IUpdateService _updates = Substitute.For<IUpdateService>();

    public AboutViewModelLinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private AboutViewModel NewVm(Action<ProcessStartInfo>? launch = null) =>
        new(_updates, new SystemReportService(new SystemInfoService(), new DiskHealthService(), new BatteryService(), new EventLogService()),
            autoCheck: false, preferences: new UpdateCheckPreferenceService(_dir), updatesDir: _dir, launch: launch ?? _launched.Add);

    /// <summary>The one address a command asked to open, which it opens through the shell, so in the browser.</summary>
    private string Opened()
    {
        var start = Assert.Single(_launched);
        Assert.True(start.UseShellExecute);
        return start.FileName;
    }

    [Theory]
    [InlineData(nameof(AboutViewModel.OpenRepoCommand), Repository)]
    [InlineData(nameof(AboutViewModel.OpenLicenseCommand), Repository + "/blob/main/LICENSE")]
    [InlineData(nameof(AboutViewModel.OpenChangelogCommand), Repository + "/blob/main/CHANGELOG.md")]
    [InlineData(nameof(AboutViewModel.OpenDiscussionsCommand), Repository + "/discussions/categories/q-a")]
    [InlineData(nameof(AboutViewModel.OpenManualDownloadCommand), Repository + "/releases/latest")]
    public void EachLink_OpensTheAddressItNames(string command, string address)
    {
        var vm = NewVm();

        Assert.IsAssignableFrom<IRelayCommand>(typeof(AboutViewModel).GetProperty(command)!.GetValue(vm)).Execute(null);

        Assert.Equal(address, Opened());
    }

    [Fact]
    public async Task ManualDownload_OpensTheReleaseTheCheckFound()
    {
        _updates.GetLatestAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<UpdateService.ReleaseInfo?>(Release));
        var vm = NewVm();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal("v99.0.0", vm.LatestVersionLabel);   // the premise: the release was found

        vm.OpenManualDownloadCommand.Execute(null);

        Assert.Equal(Release.HtmlUrl, Opened());
    }

    [Fact]
    public void AnEntryInTheHistory_OpensItsOwnRelease()
    {
        var vm = NewVm();

        vm.OpenReleaseCommand.Execute(Repository + "/releases/tag/v1.2.3");

        Assert.Equal(Repository + "/releases/tag/v1.2.3", Opened());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportAProblem_OpensTheBugFormForThisVersion_AndThisSession(bool elevated)
    {
        using var elevation = AdminHelper.ForceElevation(elevated);
        var vm = NewVm();

        vm.ReportProblemCommand.Execute(null);

        Assert.Equal(AboutViewModel.BuildBugReportUrl(UpdateService.CurrentVersion.ToString(3), isElevated: elevated), Opened());
    }

    [Fact]
    public void WithoutAWpfApplication_TheTabsOwnLauncherStartsNothing()
    {
        // A program that does not exist: started for real it throws, so a launcher that went ahead cannot pass here,
        // and there is nothing it could open.
        var missing = new ProcessStartInfo(Path.Combine(_dir, "not-a-program.exe")) { UseShellExecute = false };

        Assert.Null(Record.Exception(() => AboutViewModel.StartOnlyInTheApp(missing)));
    }

    [Fact]
    public void ALink_ThatNothingCanOpen_FailsQuietly()
    {
        // No program associated with web addresses, which is the error Windows gives then.
        var vm = NewVm(_ => throw new Win32Exception(1155, "No application is associated with the specified file for this operation."));

        Assert.Null(Record.Exception(() => vm.OpenRepoCommand.Execute(null)));
    }
}
