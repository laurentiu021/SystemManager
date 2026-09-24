// SysManager · AboutViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net.Http;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class AboutViewModelTests
{
    /// <summary>
    /// A per-run scratch directory every AboutViewModel built here is pointed at, so no test in this
    /// file can write the startup-check preference into the developer's real <c>%AppData%\SysManager</c>.
    /// </summary>
    /// <remarks>
    /// The core constructor already documented a <c>preferences</c> seam for exactly this, but the two
    /// convenience overloads did not thread it — so all 23 constructions in this file went around it,
    /// and each one rewrote the real preference file. The seam being present and documented was not
    /// enough: it has to exist on the constructor the tests actually call. Fourth instance of the shape
    /// fixed in #1772 (#1785).
    /// <para>Static and deliberately not cleaned up: it outlives every test here, there is no
    /// after-all hook, and a few bytes left in TEMP is strictly better than one byte written into the
    /// real profile.</para>
    /// </remarks>
    private static readonly string ConfigDir = CreateScratchDir();

    private static string CreateScratchDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerAboutVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Default-service AboutViewModel, redirected away from the real profile.</summary>
    private static AboutViewModel NewVm() => new(ConfigDir);

    /// <summary>
    /// Builds an AboutViewModel WITHOUT the startup update-check, so default-state
    /// assertions don't race the constructor's async network fetch (which populates
    /// UpdateStatus / LatestNotes / LatestVersionLabel / LatestPublishedLabel /
    /// UpdateAvailable).
    /// </summary>
    private static AboutViewModel NewVmNoAutoCheck() =>
        new(new UpdateService(), new SystemReportService(new SystemInfoService(), new DiskHealthService()),
            autoCheck: false, preferences: new UpdateCheckPreferenceService(ConfigDir), updatesDir: ConfigDir);

    [Fact]
    public void Constructs_WithDefaultService()
    {
        var vm = NewVm();
        Assert.NotNull(vm);
    }

    /// <summary>
    /// This overload leaves the startup check ON, so it takes a substitute rather than the real
    /// service: with the concrete class it made live calls to api.github.com from the blocking unit
    /// suite, which is what the <see cref="IUpdateService"/> seam exists to stop (#2409). The
    /// concrete type is still exercised through <see cref="NewVmNoAutoCheck"/>, which cannot call out.
    /// </summary>
    [Fact]
    public void Constructs_WithInjectedService()
    {
        var vm = new AboutViewModel(Substitute.For<IUpdateService>(), new SystemReportService(new SystemInfoService(), new DiskHealthService()), ConfigDir);
        Assert.NotNull(vm);
    }

    [Fact]
    public void CurrentVersion_NonEmpty()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.CurrentVersion));
    }

    [Fact]
    public void CurrentVersion_ParsesAsVersion()
    {
        var vm = NewVm();
        Assert.True(Version.TryParse(vm.CurrentVersion, out _));
    }

    [Fact]
    public void ReleaseHistory_StartsEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ReleaseHistory);
        // May or may not have populated yet depending on async startup —
        // just make sure the collection is there.
    }

    [Fact]
    public void UpdateStatus_HasInitialMessage()
    {
        var vm = NewVmNoAutoCheck();
        Assert.False(string.IsNullOrWhiteSpace(vm.UpdateStatus));
    }

    [Fact]
    public void UpdateAvailable_DefaultsFalse()
    {
        var vm = NewVmNoAutoCheck();
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public void IsDownloading_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public void DownloadPercent_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.DownloadPercent);
    }

    [Fact]
    public void DownloadedPath_DefaultsNull()
    {
        var vm = NewVm();
        Assert.Null(vm.DownloadedPath);
    }

    [Fact]
    public void AutoDownloadFailed_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.AutoDownloadFailed);
    }

    [Theory]
    [InlineData("CheckForUpdatesCommand")]
    [InlineData("LoadHistoryCommand")]
    [InlineData("DownloadCommand")]
    [InlineData("InstallUpdateCommand")]
    [InlineData("OpenManualDownloadCommand")]
    [InlineData("OpenRepoCommand")]
    [InlineData("OpenLicenseCommand")]
    [InlineData("OpenDownloadFolderCommand")]
    public void CommandExists(string propertyName)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    [Fact]
    public void OpenRepoCommand_DoesNotThrow()
    {
        var vm = NewVm();
        // Shell execute is wrapped in try/catch; even if no browser is
        // associated, it must not throw.
        var ex = Record.Exception(() => vm.OpenRepoCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenLicenseCommand_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.OpenLicenseCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenManualDownloadCommand_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.OpenManualDownloadCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenDownloadFolderCommand_NoPath_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.OpenDownloadFolderCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task InstallUpdateCommand_WithoutDownload_SetsErrorStatus()
    {
        var vm = new AboutViewModel(ConfigDir) { DownloadedPath = null };
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Contains("No downloaded", vm.DownloadStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallUpdateCommand_WithFakePath_SetsNoFileStatus()
    {
        var vm = new AboutViewModel(ConfigDir) { DownloadedPath = @"C:\nonexistent\fake.exe" };
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Contains("No downloaded", vm.DownloadStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallUpdateCommand_WithPathButNoRelease_SetsNoReleaseStatus()
    {
        // Create a temp file to simulate a downloaded exe
        var tmp = Path.GetTempFileName();
        try
        {
            var vm = new AboutViewModel(ConfigDir) { DownloadedPath = tmp };
            await vm.InstallUpdateCommand.ExecuteAsync(null);
            Assert.Contains("No release info", vm.DownloadStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task LoadHistoryCommand_NeverThrows()
    {
        var vm = NewVm();
        var ex = await Record.ExceptionAsync(() => ((Task?)vm.LoadHistoryCommand.ExecuteAsync(null) ?? Task.CompletedTask));
        Assert.Null(ex);
    }

    [Fact]
    public async Task CheckForUpdatesCommand_NeverThrows()
    {
        var vm = NewVm();
        var ex = await Record.ExceptionAsync(() => ((Task?)vm.CheckForUpdatesCommand.ExecuteAsync(null) ?? Task.CompletedTask));
        Assert.Null(ex);
    }

    [Fact]
    public void LatestVersionLabel_DefaultsEmpty()
    {
        var vm = NewVmNoAutoCheck();
        Assert.Equal(string.Empty, vm.LatestVersionLabel);
    }

    [Fact]
    public void LatestPublishedLabel_DefaultsEmpty()
    {
        var vm = NewVmNoAutoCheck();
        Assert.Equal(string.Empty, vm.LatestPublishedLabel);
    }

    [Fact]
    public void LatestNotes_DefaultsEmpty()
    {
        var vm = NewVmNoAutoCheck();
        Assert.Equal(string.Empty, vm.LatestNotes);
    }

    /// <summary>
    /// A history card whose release had no <c>html_url</c> must not offer a link that goes nowhere. The
    /// emptiness decision lives in <c>CanExecute</c> so it is observable here: <c>OpenUrl</c> returns early
    /// when there is no WPF <c>Application</c>, so a guard inside the command body would assert nothing.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("https://github.com/laurentiu021/SystemManager/releases/tag/v1.0.0", true)]
    public void OpenReleaseCommand_IsOfferedOnlyForAnEntryThatHasAUrl(string? url, bool expected)
    {
        var vm = NewVmNoAutoCheck();
        Assert.Equal(expected, vm.OpenReleaseCommand.CanExecute(url));
    }

    [Fact]
    public void DownloadStatus_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.DownloadStatus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    public void DownloadPercent_AcceptsFullRange(int pct)
    {
        var vm = new AboutViewModel(ConfigDir) { DownloadPercent = pct };
        Assert.Equal(pct, vm.DownloadPercent);
    }

    [Fact]
    public void BuildDate_IsString()
    {
        var vm = NewVm();
        Assert.NotNull(vm.BuildDate);
    }

    [Fact]
    public void ReleaseNote_Defaults_AreEmpty()
    {
        var r = new ReleaseNote();
        Assert.Equal(string.Empty, r.Version);
        Assert.Equal(string.Empty, r.Title);
        Assert.Equal(string.Empty, r.Body);
        Assert.Equal(string.Empty, r.Url);
        Assert.False(r.IsCurrent);
    }

    [Fact]
    public void ReleaseNote_InitSyntax_Works()
    {
        var r = new ReleaseNote { Version = "v0.5.0", Title = "Test", Body = "Body", Url = "https://u", IsCurrent = true };
        Assert.Equal("v0.5.0", r.Version);
        Assert.True(r.IsCurrent);
    }

    // ── Report export location ──
    //
    // Export used to write straight to the Desktop with no prompt, unlike every other
    // export in the app (System Report, Logs, Resource History and Profile all use
    // SaveFileDialog). It now asks first.
    //
    // Deliberately NOT unit-tested by invoking the command: SaveFileDialog is constructed
    // directly, and calling it headlessly opens a real dialog that blocks forever waiting
    // for input rather than returning false. A test that executed ExportToFileCommand
    // would hang CI. Verified by running the command in a console harness, which printed
    // its first line and then stopped at ShowDialog() until the process was killed.
    //
    // The same limitation applies to the four sibling exports, none of which are unit
    // tested either. Covering this properly needs the dialog behind an injectable seam
    // (an IFileDialogService), which is a broader refactor than a bug fix should carry.
    // Tracked separately; until then the guarantee is enforced by code review: this method
    // must not write anywhere the user did not pick.
    [Fact]
    public void ConstructingTheViewModel_DoesNotTouchTheRealPreferenceFile()
    {
        // The end-to-end guarantee, stated against the actual user path rather than a proxy. Building
        // an AboutViewModel and toggling the startup-check checkbox — which every test in this file
        // does, directly or via the constructor — must leave %AppData%\SysManager exactly as it was.
        //
        // This is the assertion that was failing silently: the core constructor documented a
        // `preferences` seam for exactly this, but the two convenience overloads the tests actually
        // call did not thread it, so all 28 constructions rewrote the developer's real file. Fails
        // against the old code, where the default constructor had no configDir to accept.
        var realPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SysManager", "update-check.json");
        var existedBefore = File.Exists(realPath);
        var contentBefore = existedBefore ? File.ReadAllText(realPath) : null;

        var vm = NewVm();
        vm.CheckForUpdatesOnStartup = false;
        vm.CheckForUpdatesOnStartup = true;

        Assert.Equal(existedBefore, File.Exists(realPath));
        if (existedBefore) Assert.Equal(contentBefore, File.ReadAllText(realPath));

        // …and it went to the redirected directory instead, so the seam is genuinely wired through
        // rather than merely accepted and ignored.
        Assert.True(File.Exists(Path.Combine(ConfigDir, "update-check.json")),
            "the preference was not written to the override directory — configDir is accepted but unused");
    }

    // ── BuildBugReportUrl (pure — the "Report a problem" pre-fill) ──
    // The Preview banner asks users to report on GitHub; these pin that the in-app link lands on the
    // right form with the two required fields pre-filled. Pre-fill is query-param based and GitHub
    // silently drops an unknown field id, so a template drift degrades to a blank field — hence a test.

    [Fact]
    public void BuildBugReportUrl_TargetsTheBugTemplateOnTheRealRepo()
    {
        var url = AboutViewModel.BuildBugReportUrl("1.63.1", isElevated: false);

        Assert.StartsWith($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/issues/new", url);
        // The field id must match .github/ISSUE_TEMPLATE/bug_report.yml, or GitHub opens a blank chooser.
        Assert.Contains("template=bug_report.yml", url);
    }

    [Fact]
    public void BuildBugReportUrl_PrefillsTheVersionField()
    {
        var url = AboutViewModel.BuildBugReportUrl("1.63.1", isElevated: false);
        Assert.Contains("version=1.63.1", url);
    }

    [Theory]
    // The dropdown option strings must match bug_report.yml exactly (spaces + parentheses, URL-encoded).
    [InlineData(true, "Yes%20%28elevated%29")]
    [InlineData(false, "No%20%28standard%20user%29")]
    public void BuildBugReportUrl_PrefillsElevationWithTheExactDropdownOption(bool elevated, string encoded)
    {
        var url = AboutViewModel.BuildBugReportUrl("1.63.1", elevated);
        Assert.Contains($"elevation={encoded}", url);
    }

    [Fact]
    public void BuildBugReportUrl_EncodesTheValues_NoRawSpacesOrParens()
    {
        // A raw space or bracket in a URL is invalid and some launchers truncate at it, dropping the
        // pre-fill silently. The query must be fully encoded.
        var url = AboutViewModel.BuildBugReportUrl("1.63.1", isElevated: true);
        var query = url[(url.IndexOf('?') + 1)..];
        Assert.DoesNotContain(' ', query);
        Assert.DoesNotContain('(', query);
        Assert.DoesNotContain(')', query);
    }

    // ── QuestionsUrl (the "Ask a question" button) ──
    // The button used to open the Discussions root, which every release fills with an auto-posted
    // announcement — so a user looking for the question box landed in a wall of changelogs.

    [Fact]
    public void QuestionsUrl_DeepLinksTheQAndACategory_NotTheDiscussionsRoot()
    {
        var url = AboutViewModel.QuestionsUrl;

        Assert.Equal(
            $"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/discussions/categories/q-a",
            url);
        // The regression, stated as its own assertion: the root is not an acceptable answer.
        Assert.NotEqual(
            $"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/discussions",
            url);
    }
}

/// <summary>
/// The startup update-check gate, as the view-model applies it.
/// <para><see cref="UpdateCheckPreferenceServiceTests"/> covers the decision in isolation; these
/// cover the wiring, which is where the defect was — the check was hardcoded on with no setting and
/// no memory of the previous run, so every launch made two calls to api.github.com.</para>
/// <para>The view-model takes <see cref="IUpdateService"/>, so every test here runs against a
/// substitute and the request itself IS counted: the gated tests assert the calls were never made,
/// not merely that the resulting state stayed empty. Before that seam existed (#2409) the two
/// open-gate tests below had no choice but to let the blocking unit suite call api.github.com for
/// real, and what they asserted depended on whether that round trip returned before the test ended —
/// which is exactly how #2407's race came to fail on main and pass on its own PR run. Each test also
/// injects a temp directory, so the developer's own preference file is never read or written.</para>
/// </summary>
public sealed class AboutViewModelUpdateGateTests : IDisposable
{
    private readonly string _dir;

    public AboutViewModelUpdateGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerAboutGateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A release the substitute can hand back, so the check takes its success path.</summary>
    private static UpdateService.ReleaseInfo Release() => new(
        new Version(1, 0, 0), "v1.0.0", "SysManager v1.0.0", "notes",
        DateTimeOffset.UnixEpoch, "https://github.com/laurentiu021/SystemManager/releases/tag/v1.0.0",
        AssetUrl: null, AssetSize: null);

    /// <summary>
    /// A substitute that answers instantly and counts what it was asked. Returning a real release
    /// rather than leaving the calls unconfigured keeps the check on its SUCCESS path, so the
    /// recorder runs — which is the ordering the persistence test below depends on.
    /// </summary>
    private static IUpdateService NewUpdates()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.GetLatestAsync(Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<UpdateService.ReleaseInfo?>(Release()));
        updates.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<UpdateService.ReleaseInfo>>([Release()]));
        return updates;
    }

    /// <summary>
    /// A substitute that fails the way the real service fails. <c>GetLatestAsync</c> has a catch-all
    /// and converts every error into <c>null</c> plus a <c>LastError</c> string, so "the check
    /// failed" is a null return and NOT a thrown exception — returning null here is the faithful
    /// double, and configuring it explicitly rather than leaving the member unconfigured keeps the
    /// test from depending on what NSubstitute picks for an auto-value.
    /// </summary>
    private static IUpdateService NewFailingUpdates()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.GetLatestAsync(Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<UpdateService.ReleaseInfo?>(null));
        updates.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<UpdateService.ReleaseInfo>>([]));
        updates.LastError.Returns("Network: No such host is known.");
        return updates;
    }

    /// <summary>
    /// autoCheck stays TRUE on purpose: the preference — not that flag — has to be what stops the
    /// call. Passing false would test nothing.
    /// </summary>
    private AboutViewModel NewVm(UpdateCheckPreferenceService preferences, IUpdateService updates) =>
        new(updates,
            new SystemReportService(new SystemInfoService(), new DiskHealthService()),
            autoCheck: true,
            preferences);

    [Fact]
    public async Task WithTheCheckTurnedOff_NoVersionIsFetchedAndTheReasonIsShown()
    {
        var prefs = new UpdateCheckPreferenceService(_dir);
        prefs.SetCheckOnStartup(false);
        var updates = NewUpdates();

        using var vm = NewVm(prefs, updates);
        await vm.InitializationComplete;

        Assert.False(vm.CheckForUpdatesOnStartup);
        // The call was never made — asserted directly, not inferred from the empty label.
        await updates.DidNotReceive().GetLatestAsync(Arg.Any<CancellationToken>());
        await updates.DidNotReceive().GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Empty(vm.LatestVersionLabel);        // nothing came back, because nothing was asked
        Assert.False(vm.UpdateCheckFailed);         // and it is not presented as an error
        Assert.Contains("off", vm.UpdateStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithARecentCheck_TheStartupCallIsSkippedButTheSettingStaysOn()
    {
        var prefs = new UpdateCheckPreferenceService(_dir);
        prefs.RecordCheck(DateTimeOffset.UtcNow);
        var updates = NewUpdates();

        using var vm = NewVm(prefs, updates);
        await vm.InitializationComplete;

        Assert.True(vm.CheckForUpdatesOnStartup);   // throttled is not the same as disabled
        await updates.DidNotReceive().GetLatestAsync(Arg.Any<CancellationToken>());
        Assert.False(vm.UpdateCheckFailed);
        Assert.Contains("recently", vm.UpdateStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCheckboxReflectsTheStoredPreference()
    {
        var prefs = new UpdateCheckPreferenceService(_dir);
        prefs.SetCheckOnStartup(false);

        using var vm = NewVm(prefs, NewUpdates());

        Assert.False(vm.CheckForUpdatesOnStartup);
    }

    [Fact]
    public async Task TogglingTheCheckbox_PersistsTheChoice()
    {
        var prefs = new UpdateCheckPreferenceService(_dir);
        using var vm = NewVm(prefs, NewUpdates());

        // Drain the startup check FIRST, so the recorder has already written and the user's toggle is
        // the last writer. That ordering used to depend on whether a live GitHub call came back inside
        // the test's lifetime; against a substitute it is decided here. #2407 serialized the pair, and
        // this asserts the half of its table that matters to the user: Record, then Set.
        await vm.InitializationComplete;
        vm.CheckForUpdatesOnStartup = false;

        Assert.False(new UpdateCheckPreferenceService(_dir).Load().CheckOnStartup);
    }

    [Fact]
    public async Task LoadingThePreference_DoesNotRewriteTheFile()
    {
        // The constructor assigns the bound property, which would otherwise fire the save handler
        // and rewrite the file on every launch — including for a user who never touched the setting.
        var prefs = new UpdateCheckPreferenceService(_dir);
        var path = Path.Combine(_dir, UpdateCheckPreferenceService.FileName);
        Assert.False(File.Exists(path));

        // Hold the check open so the assert cannot race the recorder. What must not write is the
        // CONSTRUCTOR; RecordCheck writing afterwards is correct, and the second assert says so.
        // Previously the window was whatever a real GitHub round trip happened to take.
        var held = new TaskCompletionSource<UpdateService.ReleaseInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = NewUpdates();
        updates.GetLatestAsync(Arg.Any<CancellationToken>()).Returns(held.Task);

        using var vm = NewVm(prefs, updates);

        Assert.False(File.Exists(path));

        // Let the held call finish and drain the check, so nothing is still writing into the temp
        // directory after Dispose deletes it.
        held.SetResult(Release());
        await vm.InitializationComplete;

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AFailedCheck_DoesNotStartTheThrottle()
    {
        // #2408: the recorder ran unconditionally, so one launch with no network spent the user's
        // whole 24h allowance on a check that answered nothing. The next launch then reported
        // "Checked recently" — the worst of both, since there was no version to show either.
        var prefs = new UpdateCheckPreferenceService(_dir);
        var updates = NewFailingUpdates();

        using var vm = NewVm(prefs, updates);
        await vm.InitializationComplete;

        // The call really was made — otherwise this would pass for the wrong reason, by way of the
        // gate that the tests above cover.
        await updates.Received(1).GetLatestAsync(Arg.Any<CancellationToken>());
        Assert.True(vm.UpdateCheckFailed);
        Assert.Contains("Couldn't reach GitHub", vm.UpdateStatus, StringComparison.Ordinal);

        // The timestamp, and then the decision it feeds: the next launch tries again.
        var stored = new UpdateCheckPreferenceService(_dir).Load();
        Assert.Null(stored.LastCheckUtc);
        Assert.True(UpdateCheckPreferenceService.ShouldCheckAtStartup(stored, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ACheckThatGotAnAnswer_StartsTheThrottle()
    {
        // The other half of the same conditional. Without this, "never record" would satisfy the
        // test above and silently restore the two-calls-per-launch behaviour the gate exists to stop.
        var prefs = new UpdateCheckPreferenceService(_dir);

        using var vm = NewVm(prefs, NewUpdates());
        await vm.InitializationComplete;

        Assert.False(vm.UpdateCheckFailed);
        var stored = new UpdateCheckPreferenceService(_dir).Load();
        Assert.NotNull(stored.LastCheckUtc);
        Assert.False(UpdateCheckPreferenceService.ShouldCheckAtStartup(stored, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A history load that failed on its own does not block the record — decided deliberately, not by
    /// accident. The clock rate-limits the latest-version question, and that one was answered;
    /// blocking here would re-check the version on every launch for as long as the history call
    /// happened to be failing. <c>HistoryUnavailable</c> tells the user the notes are missing and
    /// Refresh reloads them, neither of which needs a 24h wait.
    /// <para>Both parameters matter beyond this decision: <c>LoadHistoryAsync</c>'s two catch blocks
    /// were unreachable from the real service — <c>GetRecentAsync</c> swallows both failures and
    /// returns an empty list — so until <see cref="IUpdateService"/> existed (#2409) only
    /// <c>ArchitectureTests.EveryEmptyStateFlag_IsSetWhereItsDataArrives</c> could pin them, by source
    /// shape. These two cases are the first to drive them for real.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]     // HttpRequestException — GitHub could not be reached
    [InlineData(true)]      // TaskCanceledException — the request timed out
    public async Task WhenOnlyTheHistoryFails_TheThrottleStillStarts(bool timedOut)
    {
        Exception failure = timedOut
            ? new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
            : new HttpRequestException("No such host is known.");

        var prefs = new UpdateCheckPreferenceService(_dir);
        var updates = NewUpdates();
        updates.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromException<IReadOnlyList<UpdateService.ReleaseInfo>>(failure));

        using var vm = NewVm(prefs, updates);
        await vm.InitializationComplete;

        Assert.False(vm.UpdateCheckFailed);     // the version answer arrived
        Assert.True(vm.HistoryUnavailable);     // the notes did not
        Assert.NotNull(new UpdateCheckPreferenceService(_dir).Load().LastCheckUtc);
    }
}

/// <summary>
/// Tests for the rollback offer on the About tab.
/// <para>The updater was the one mutating feature in the app with no way back: the atomic move that
/// makes an INTERRUPTED update safe also destroyed the outgoing executable, so a SUCCESSFUL update
/// into a broken build left the user with nothing to return to. A winget user can
/// <c>winget install --version</c>; an in-app updater user had to find an older GitHub release on
/// their own, which for the target persona is a dead end.</para>
/// <para>Each test injects a temp updates directory, so the check never reads — or comes to depend
/// on — whatever happens to be in the developer's real profile.</para>
/// </summary>
public sealed class AboutViewModelRollbackTests : IDisposable
{
    private readonly string _dir;

    public AboutViewModelRollbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerAboutRollbackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        GC.SuppressFinalize(this);
    }

    // autoCheck: false — these assert constructor state, so they must not race the network fetch.
    private AboutViewModel NewVm() =>
        new(new UpdateService(),
            new SystemReportService(new SystemInfoService(), new DiskHealthService()),
            autoCheck: false,
            preferences: new UpdateCheckPreferenceService(_dir),
            updatesDir: _dir);

    private string PreviousBuild => Path.Combine(_dir, UpdateApplier.PreviousBuildFileName);

    /// <summary>
    /// Writes a retained build the way the applier does — the binary AND its checksum. A rollback is
    /// only offered when both exist, because SysManager will not start a saved build it cannot verify,
    /// so writing the binary alone sets up the legacy/tampered state rather than the healthy one.
    /// </summary>
    private void RetainBuild(string content = "OLD-BUILD")
    {
        File.WriteAllText(PreviousBuild, content);
        File.WriteAllText(
            UpdateApplier.PreviousBuildHashPath(_dir),
            UpdateApplier.ComputeFileHash(PreviousBuild));
    }

    [Fact]
    public void CanRollBack_IsFalse_WhenNoPreviousBuildWasRetained()
    {
        // A fresh install, or a user who has never updated: the button must not appear, because
        // pressing it could do nothing.
        Assert.False(File.Exists(PreviousBuild));

        using var vm = NewVm();

        Assert.False(vm.CanRollBack);
    }

    [Fact]
    public void CanRollBack_IsTrue_WhenAPreviousBuildExists()
    {
        RetainBuild();

        using var vm = NewVm();

        Assert.True(vm.CanRollBack);
    }

    [Fact]
    public void CanRollBack_IsFalse_WhenTheRetainedBuildHasNoChecksum()
    {
        // A build retained by a version that predates the checksum, or one an attacker dropped in.
        // SysManager will not start a saved build it cannot verify, so the offer must not appear at all
        // — being offered a button that then refuses is worse than not seeing it.
        File.WriteAllText(PreviousBuild, "OLD-BUILD");   // binary only, deliberately no checksum
        Assert.False(File.Exists(UpdateApplier.PreviousBuildHashPath(_dir)));

        using var vm = NewVm();

        Assert.False(vm.CanRollBack);
    }

    [Fact]
    public void RollBackCommand_Exists()
    {
        using var vm = NewVm();
        Assert.NotNull(vm.RollBackCommand);
    }

    [Fact]
    public async Task RollBack_WhenThePreviousBuildVanished_SaysSo_AndHidesTheOffer()
    {
        // The file can disappear between the button appearing and being pressed (manual cleanup, disk
        // tools, another instance). That must produce an explanation and a corrected UI rather than a
        // silent no-op or a crash.
        RetainBuild();
        using var vm = NewVm();
        Assert.True(vm.CanRollBack);

        File.Delete(PreviousBuild);
        await vm.RollBackCommand.ExecuteAsync(null);

        Assert.False(vm.CanRollBack);
        Assert.Contains("no longer available", vm.RollBackStatus);
    }

    [Fact]
    public async Task RollBack_WhenTheRetainedBuildWasSwapped_RefusesAndExplains()
    {
        // The binary is still there and its checksum is still there, but the bytes no longer match —
        // exactly what a same-user attacker replacing the saved copy looks like. Starting it would run
        // their payload with SysManager's token, so this must refuse and say so rather than launch.
        RetainBuild();
        using var vm = NewVm();
        Assert.True(vm.CanRollBack);

        File.WriteAllText(PreviousBuild, "ATTACKER-PAYLOAD");
        await vm.RollBackCommand.ExecuteAsync(null);

        Assert.Contains("Cannot go back safely", vm.RollBackStatus);
        Assert.Contains("changed", vm.RollBackStatus);
    }

    [Fact]
    public void RollBackLabel_IsPlainLanguage_NotAMechanism()
    {
        // The target user does not think in terms of executables or version numbers on disk.
        using var vm = NewVm();

        Assert.Contains("previous version", vm.RollBackLabel);
        Assert.DoesNotContain(".exe", vm.RollBackLabel);
    }

    [Fact]
    public void AboutView_RendersTheRollbackButtonAndItsStatus()
    {
        // CanRollBack / RollBackStatus existing on the ViewModel proves nothing if the view never
        // binds them — that is precisely the dead-property class of defect this codebase has hit
        // repeatedly. Assert against the shipped markup.
        var xaml = File.ReadAllText(TestPaths.AppFile("Views", "AboutView.xaml"));

        Assert.Contains("RollBackCommand", xaml);
        Assert.Contains("CanRollBack", xaml);      // gates visibility
        Assert.Contains("RollBackLabel", xaml);
        Assert.Contains("RollBackStatus", xaml);   // feedback is rendered, not dead
    }

    [Fact]
    public void AFailedDownload_HasSomewhereVisibleToSayWhy()
    {
        // DownloadStatus was rendered in exactly two places, one gated on IsDownloading and one on
        // DownloadedPath, and a failure leaves BOTH false: IsDownloading is cleared in the command's
        // finally, and DownloadedPath is never set. So all four failure messages — the ones naming a
        // firewall, an unavailable server, a timeout — were assigned to elements that had just gone
        // invisible, and the only signal the user got was the Manual download button appearing (#2281).
        //
        // Both halves are asserted, because either alone passes on the broken code: the markup needs a
        // renderer whose gate a failure SETS, and the command has to actually set it. A substring check
        // for "DownloadStatus" would have passed before the fix, since the binding was already there.
        var root = System.Xml.Linq.XDocument.Load(TestPaths.AppFile("Views", "AboutView.xaml")).Root;
        Assert.NotNull(root);

        var parents = root!.Descendants()
            .SelectMany(p => p.Elements().Select(c => (Child: c, Parent: p)))
            .ToDictionary(x => x.Child, x => x.Parent);

        // Every gate above each element that renders DownloadStatus.
        var gateChains = root.Descendants()
            .Where(e => (string?)e.Attribute("Text") == "{Binding DownloadStatus}")
            .Select(e =>
            {
                var gates = new List<string>();
                for (var n = e; n is not null; n = parents.GetValueOrDefault(n))
                    if ((string?)n.Attribute("Visibility") is { } v)
                        gates.Add(v);
                return gates;
            })
            .ToList();

        Assert.True(gateChains.Count >= 3,
            $"only {gateChains.Count} elements render DownloadStatus — expected the in-progress line, the "
            + "success row and the failure row, so this test is no longer looking at what it thinks.");

        Assert.Contains(gateChains, chain =>
            chain.Any(g => g.Contains("AutoDownloadFailed", StringComparison.Ordinal)));

        // …and the flag that gate depends on is set by every failure path, or the row above can never
        // appear. Read from the source because the command needs a release and a network to run.
        var command = MethodBody(File.ReadAllText(TestPaths.AppFile("ViewModels", "AboutViewModel.cs")),
                                 "private async Task DownloadAsync()");
        var failureWrites = System.Text.RegularExpressions.Regex
            .Matches(command, @"AutoDownloadFailed\s*=\s*true").Count;
        Assert.Equal(4, failureWrites);   // returned-nothing, HttpRequestException, IOException, timeout
    }

    /// <summary>The body of a method, delimited by counting braces from its signature.</summary>
    private static string MethodBody(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{signature} not found — the test would assert over the whole file");

        var open = source.IndexOf('{', at);
        var close = SourceBraces.MatchingBrace(source, open);

        return close < 0 ? source[open..] : source[open..(close + 1)];
    }
}

/// <summary>
/// Which release-history row the About tab marks as the running build.
/// <para>#2418: the row was marked with <c>r.Version == UpdateService.CurrentVersion</c>, and that comparison
/// could never be true. A row's version is parsed from its tag, which has three components, so its
/// <see cref="Version.Revision"/> is unspecified (-1); <see cref="UpdateService.CurrentVersion"/> comes from
/// <c>AssemblyVersion</c>, which always carries a fourth component of 0. <see cref="Version"/>'s equality
/// compares all four as stored, so the two never matched and the "Current" badge bound to
/// <see cref="ReleaseNote.IsCurrent"/> never appeared, from the About tab's first release onwards.</para>
/// <para>The tests above could not see it because they only ever build a <see cref="ReleaseNote"/> by hand
/// with <c>IsCurrent</c> already set: that covers the property, never the comparison that computes it.
/// These go through <c>LoadHistoryAsync</c> itself.</para>
/// </summary>
public sealed class AboutViewModelReleaseHistoryTests : IDisposable
{
    private readonly string _dir;

    public AboutViewModelReleaseHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerAboutHistoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A release as the real service hands it back: the version is PARSED from the tag, the way
    /// <c>UpdateService.Map</c> builds it, so the row carries the component count a real row carries rather
    /// than whatever a hand-built <see cref="Version"/> happens to have.
    /// </summary>
    private static UpdateService.ReleaseInfo Release(string tag) => new(
        UpdateService.ParseVersion(tag) ?? throw new ArgumentException($"'{tag}' does not parse", nameof(tag)),
        tag, $"SysManager {tag}", "notes", DateTimeOffset.UnixEpoch,
        $"https://github.com/laurentiu021/SystemManager/releases/tag/{tag}",
        AssetUrl: null, AssetSize: null);

    // autoCheck: false — the history load is driven explicitly below, so the startup check must not run a
    // second one behind the test's back.
    private AboutViewModel NewVm(IUpdateService updates) =>
        new(updates,
            new SystemReportService(new SystemInfoService(), new DiskHealthService()),
            autoCheck: false,
            preferences: new UpdateCheckPreferenceService(_dir),
            updatesDir: _dir);

    [Fact]
    public async Task TheRowNamingTheRunningRelease_IsTheOneMarkedCurrent()
    {
        var running = UpdateService.CurrentVersion;
        var runningTag = $"v{running.ToString(3)}";
        var newerTag = $"v{running.Major + 1}.0.0";
        const string olderTag = "v0.1.0";

        // The premise, checked rather than assumed: the two sides really do differ in component count, which
        // is the whole defect. Without these, a build whose AssemblyVersion lost its fourth component would
        // keep this test green while it stopped testing what it is named for.
        Assert.True(running.Revision >= 0,
            $"CurrentVersion {running} has no fourth component, so this no longer exercises #2418");
        Assert.Equal(-1, UpdateService.ParseVersion(runningTag)!.Revision);

        var updates = Substitute.For<IUpdateService>();
        updates.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<UpdateService.ReleaseInfo>>(
                   [Release(newerTag), Release(runningTag), Release(olderTag)]));

        using var vm = NewVm(updates);
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.ReleaseHistory.Count);
        var current = Assert.Single(vm.ReleaseHistory, r => r.IsCurrent);
        Assert.Equal(runningTag, current.Version);
    }
}
