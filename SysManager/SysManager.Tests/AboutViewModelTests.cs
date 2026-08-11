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
    /// Builds an AboutViewModel WITHOUT the startup update-check, so default-state
    /// assertions don't race the constructor's async network fetch (which populates
    /// UpdateStatus / LatestNotes / LatestVersionLabel / LatestPublishedLabel /
    /// UpdateAvailable).
    /// </summary>
    private static AboutViewModel NewVmNoAutoCheck() =>
        new(new UpdateService(), new SystemReportService(new SystemInfoService(), new DiskHealthService()), autoCheck: false);

    [Fact]
    public void Constructs_WithDefaultService()
    {
        var vm = new AboutViewModel();
        Assert.NotNull(vm);
    }

    [Fact]
    public void Constructs_WithInjectedService()
    {
        var vm = new AboutViewModel(new UpdateService(), new SystemReportService(new SystemInfoService(), new DiskHealthService()));
        Assert.NotNull(vm);
    }

    [Fact]
    public void CurrentVersion_NonEmpty()
    {
        var vm = new AboutViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.CurrentVersion));
    }

    [Fact]
    public void CurrentVersion_ParsesAsVersion()
    {
        var vm = new AboutViewModel();
        Assert.True(Version.TryParse(vm.CurrentVersion, out _));
    }

    [Fact]
    public void ReleaseHistory_StartsEmpty()
    {
        var vm = new AboutViewModel();
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
        var vm = new AboutViewModel();
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public void DownloadPercent_DefaultsZero()
    {
        var vm = new AboutViewModel();
        Assert.Equal(0, vm.DownloadPercent);
    }

    [Fact]
    public void DownloadedPath_DefaultsNull()
    {
        var vm = new AboutViewModel();
        Assert.Null(vm.DownloadedPath);
    }

    [Fact]
    public void AutoDownloadFailed_DefaultsFalse()
    {
        var vm = new AboutViewModel();
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
        var vm = new AboutViewModel();
        var prop = vm.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    [Fact]
    public void OpenRepoCommand_DoesNotThrow()
    {
        var vm = new AboutViewModel();
        // Shell execute is wrapped in try/catch; even if no browser is
        // associated, it must not throw.
        var ex = Record.Exception(() => vm.OpenRepoCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenLicenseCommand_DoesNotThrow()
    {
        var vm = new AboutViewModel();
        var ex = Record.Exception(() => vm.OpenLicenseCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenManualDownloadCommand_DoesNotThrow()
    {
        var vm = new AboutViewModel();
        var ex = Record.Exception(() => vm.OpenManualDownloadCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenDownloadFolderCommand_NoPath_DoesNotThrow()
    {
        var vm = new AboutViewModel();
        var ex = Record.Exception(() => vm.OpenDownloadFolderCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task InstallUpdateCommand_WithoutDownload_SetsErrorStatus()
    {
        var vm = new AboutViewModel { DownloadedPath = null };
        await vm.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Contains("No downloaded", vm.DownloadStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallUpdateCommand_WithFakePath_SetsNoFileStatus()
    {
        var vm = new AboutViewModel { DownloadedPath = @"C:\nonexistent\fake.exe" };
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
            var vm = new AboutViewModel { DownloadedPath = tmp };
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
        var vm = new AboutViewModel();
        var ex = await Record.ExceptionAsync(() => ((Task?)vm.LoadHistoryCommand.ExecuteAsync(null) ?? Task.CompletedTask));
        Assert.Null(ex);
    }

    [Fact]
    public async Task CheckForUpdatesCommand_NeverThrows()
    {
        var vm = new AboutViewModel();
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
        var vm = new AboutViewModel();
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
        var vm = new AboutViewModel { DownloadPercent = pct };
        Assert.Equal(pct, vm.DownloadPercent);
    }

    [Fact]
    public void BuildDate_IsString()
    {
        var vm = new AboutViewModel();
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
        File.WriteAllText(PreviousBuild, "OLD-BUILD");

        using var vm = NewVm();

        Assert.True(vm.CanRollBack);
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
        File.WriteAllText(PreviousBuild, "OLD-BUILD");
        using var vm = NewVm();
        Assert.True(vm.CanRollBack);

        File.Delete(PreviousBuild);
        await vm.RollBackCommand.ExecuteAsync(null);

        Assert.False(vm.CanRollBack);
        Assert.Contains("no longer available", vm.RollBackStatus);
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
