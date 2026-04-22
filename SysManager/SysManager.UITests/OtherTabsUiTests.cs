// SysManager · OtherTabsUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.UITests;

/// <summary>
/// Coverage for the remaining tabs (Cleanup, Drivers, Windows Update,
/// System health, App updates) — button and label presence.
/// </summary>
[Collection("App")]
public class OtherTabsUiTests
{
    private readonly AppFixture _fx;
    public OtherTabsUiTests(AppFixture fx) => _fx = fx;

    // ---------------- Cleanup ----------------

    [Fact]
    public void Cleanup_CleanTempButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButton("Clean TEMP"));
    }

    [Fact]
    public void Cleanup_EmptyRecycleBinButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButton("Empty Recycle Bin"));
    }

    [Fact]
    public void Cleanup_SfcButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButton("SFC /scannow"));
    }

    [Fact]
    public void Cleanup_DismButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButton("DISM RestoreHealth"));
    }

    [Fact]
    public void Cleanup_CancelButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButton("Cancel"));
    }

    // ---------------- Drivers ----------------

    [Fact]
    public void Drivers_ListButton_Exists()
    {
        _fx.GoToTab("nav-drivers");
        Assert.NotNull(_fx.FindButton("List installed drivers"));
    }

    [Fact]
    public void Drivers_CheckUpdatesButton_Exists()
    {
        _fx.GoToTab("nav-drivers");
        Assert.NotNull(_fx.FindButton("Check driver updates (Windows Update)"));
    }

    // ---------------- Windows Update ----------------

    [Fact]
    public void WindowsUpdate_CheckModuleButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButton("Check module"));
    }

    [Fact]
    public void WindowsUpdate_InstallModuleButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButton("Install PSWindowsUpdate"));
    }

    [Fact]
    public void WindowsUpdate_ListUpdatesButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButton("List updates"));
    }

    [Fact]
    public void WindowsUpdate_HistoryButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButton("History"));
    }

    [Fact]
    public void WindowsUpdate_PendingRebootButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButton("Pending reboot?"));
    }

    [Fact]
    public void WindowsUpdate_InstallUpdatesButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButton("Install updates"));
    }

    // ---------------- System health ----------------

    [Fact]
    public void SystemHealth_ScanButton_Exists()
    {
        _fx.GoToTab("nav-system-health");
        Assert.NotNull(_fx.FindButton("Rescan"));
    }

    [Fact]
    public void SystemHealth_DiskHealthButton_Exists()
    {
        _fx.GoToTab("nav-system-health");
        Assert.NotNull(_fx.FindButton("Run SMART check"));
    }

    [Fact]
    public void SystemHealth_MemoryCheckButton_Exists()
    {
        _fx.GoToTab("nav-system-health");
        Assert.NotNull(_fx.FindButton("Check memory errors"));
    }

    [Fact]
    public void SystemHealth_RunMemTestButton_Exists()
    {
        _fx.GoToTab("nav-system-health");
        Assert.NotNull(_fx.FindButton("Run MemTest (reboot)"));
    }

    // ---------------- App updates ----------------

    [Fact]
    public void AppUpdates_ScanButton_Exists()
    {
        _fx.GoToTab("nav-app-updates");
        Assert.NotNull(_fx.FindButton("Scan for updates"));
    }
}
