// SysManager · AboutViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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

    [Fact]
    public void Constructs_WithInjectedService()
    {
        var vm = new AboutViewModel(new UpdateService(), new SystemReportService(new SystemInfoService(), new DiskHealthService()), ConfigDir);
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
/// <para><see cref="UpdateService"/> is sealed with no interface, so the request itself cannot be
/// counted. What IS observable is that the gated path never populates the update state and explains
/// why, which is what these assert. Each test injects a temp directory, so the developer's own
/// preference file is never read or written.</para>
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

    /// <summary>
    /// autoCheck stays TRUE on purpose: the preference — not that flag — has to be what stops the
    /// call. Passing false would test nothing.
    /// </summary>
    private AboutViewModel NewVm(UpdateCheckPreferenceService preferences) =>
        new(new UpdateService(),
            new SystemReportService(new SystemInfoService(), new DiskHealthService()),
            autoCheck: true,
            preferences);

    [Fact]
    public async Task WithTheCheckTurnedOff_NoVersionIsFetchedAndTheReasonIsShown()
    {
        var prefs = new UpdateCheckPreferenceService(_dir);
        prefs.SetCheckOnStartup(false);

        using var vm = NewVm(prefs);
        await vm.InitializationComplete;

        Assert.False(vm.CheckForUpdatesOnStartup);
        Assert.Empty(vm.LatestVersionLabel);        // nothing came back, because nothing was asked
        Assert.False(vm.UpdateCheckFailed);         // and it is not presented as an error
        Assert.Contains("off", vm.UpdateStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithARecentCheck_TheStartupCallIsSkippedButTheSettingStaysOn()
    {
        var prefs = new UpdateCheckPreferenceService(_dir);
        prefs.RecordCheck(DateTimeOffset.UtcNow);

        using var vm = NewVm(prefs);
        await vm.InitializationComplete;

        Assert.True(vm.CheckForUpdatesOnStartup);   // throttled is not the same as disabled
        Assert.False(vm.UpdateCheckFailed);
        Assert.Contains("recently", vm.UpdateStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCheckboxReflectsTheStoredPreference()
    {
        var prefs = new UpdateCheckPreferenceService(_dir);
        prefs.SetCheckOnStartup(false);

        using var vm = NewVm(prefs);

        Assert.False(vm.CheckForUpdatesOnStartup);
    }

    [Fact]
    public void TogglingTheCheckbox_PersistsTheChoice()
    {
        var prefs = new UpdateCheckPreferenceService(_dir);
        using var vm = NewVm(prefs);

        vm.CheckForUpdatesOnStartup = false;

        Assert.False(new UpdateCheckPreferenceService(_dir).Load().CheckOnStartup);
    }

    [Fact]
    public void LoadingThePreference_DoesNotRewriteTheFile()
    {
        // The constructor assigns the bound property, which would otherwise fire the save handler
        // and rewrite the file on every launch — including for a user who never touched the setting.
        var prefs = new UpdateCheckPreferenceService(_dir);
        var path = Path.Combine(_dir, UpdateCheckPreferenceService.FileName);
        Assert.False(File.Exists(path));

        using var vm = NewVm(prefs);

        Assert.False(File.Exists(path));
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
        var xaml = File.ReadAllText(ViewPath("AboutView.xaml"));

        Assert.Contains("RollBackCommand", xaml);
        Assert.Contains("CanRollBack", xaml);      // gates visibility
        Assert.Contains("RollBackLabel", xaml);
        Assert.Contains("RollBackStatus", xaml);   // feedback is rendered, not dead
    }

    // Walks up from the test binaries to the app project — .xaml is not copied to the output.
    private static string ViewPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else the assertions above would silently test nothing
        var path = Path.Combine(dir!.FullName, "SysManager", "Views", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }
}
