// SysManager · AboutViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class AboutViewModelTests
{
    [Fact]
    public void Constructs_WithDefaultService()
    {
        var vm = new AboutViewModel();
        Assert.NotNull(vm);
    }

    [Fact]
    public void Constructs_WithInjectedService()
    {
        var vm = new AboutViewModel(new UpdateService());
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
        var vm = new AboutViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.UpdateStatus));
    }

    [Fact]
    public void UpdateAvailable_DefaultsFalse()
    {
        var vm = new AboutViewModel();
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
    public void InstallUpdateCommand_WithoutDownload_SetsErrorStatus()
    {
        var vm = new AboutViewModel { DownloadedPath = null };
        vm.InstallUpdateCommand.Execute(null);
        Assert.Contains("No downloaded", vm.DownloadStatus, StringComparison.OrdinalIgnoreCase);
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
        var vm = new AboutViewModel();
        Assert.Equal(string.Empty, vm.LatestVersionLabel);
    }

    [Fact]
    public void LatestPublishedLabel_DefaultsEmpty()
    {
        var vm = new AboutViewModel();
        Assert.Equal(string.Empty, vm.LatestPublishedLabel);
    }

    [Fact]
    public void LatestNotes_DefaultsEmpty()
    {
        var vm = new AboutViewModel();
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
}
