// SysManager · BrowserCleanerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for the toast <see cref="BrowserCleanerViewModel"/> shows after a clean (#2456). The rescan replaces the
/// status line at once, so the toast is often the only result the user sees.
/// </summary>
public class BrowserCleanerViewModelTests
{
    [Fact]
    public void CleanToast_WhenNothingWasRemoved_DoesNotSayTheDataWasCleaned()
    {
        // An open browser can hold every file. The toast said "Browser data cleaned — 0 files removed."
        var (title, detail) = BrowserCleanerViewModel.CleanToast(0);

        Assert.Equal("Nothing was removed", title);
        Assert.Contains("Close it and clean again", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanToast_WhenFilesWereRemoved_SaysHowMany()
        => Assert.Equal(("Browser data cleaned", "12 files removed."), BrowserCleanerViewModel.CleanToast(12));
}
