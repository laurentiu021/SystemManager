// SysManager · OtherTabsUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
        Assert.NotNull(_fx.FindButtonById("btn-cleanup-clean-temp"));
    }

    [Fact]
    public void Cleanup_EmptyRecycleBinButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButtonById("btn-cleanup-empty-recycle-bin"));
    }

    [Fact]
    public void Cleanup_SfcButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButtonById("btn-cleanup-sfc"));
    }

    [Fact]
    public void Cleanup_DismButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButtonById("btn-cleanup-dism"));
    }

    [Fact]
    public void Cleanup_CancelButton_Exists()
    {
        _fx.GoToTab("nav-cleanup");
        Assert.NotNull(_fx.FindButtonById("btn-cleanup-cancel"));
    }

    // ---------------- Drivers ----------------

    [Fact]
    public void Drivers_ListButton_Exists()
    {
        _fx.GoToTab("nav-drivers");
        Assert.NotNull(_fx.FindButtonById("btn-drivers-list"));
    }

    // ---------------- Windows Update ----------------

    [Fact]
    public void WindowsUpdate_ModuleAvailability_IsDeferredUntilHistory()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.WaitForTextInCurrentTab("used for the History view only"));
        Assert.Null(_fx.FindButtonById("btn-windows-update-install-module", timeoutSeconds: 1));
    }

    [Fact]
    public void WindowsUpdate_ListUpdatesButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButtonById("btn-windows-update-list"));
    }

    [Fact]
    public void WindowsUpdate_HistoryButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButtonById("btn-windows-update-history"));
    }

    [Fact]
    public void WindowsUpdate_PendingRebootButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButtonById("btn-windows-update-pending-reboot"));
    }

    [Fact]
    public void WindowsUpdate_InstallUpdatesButton_Exists()
    {
        _fx.GoToTab("nav-windows-update");
        Assert.NotNull(_fx.FindButtonById("btn-windows-update-install-selected"));
    }

    // ---------------- System health ----------------

    [Fact]
    public void SystemHealth_ScanButton_Exists()
    {
        _fx.GoToTab("nav-system-health");
        Assert.NotNull(_fx.FindButtonById("btn-system-health-scan"));
    }

    [Fact]
    public void SystemHealth_DiskHealthButton_Exists()
    {
        _fx.GoToTab("nav-system-health");
        Assert.NotNull(_fx.FindButtonById("btn-system-health-smart"));
    }

    [Fact]
    public void SystemHealth_MemoryCheckButton_Exists()
    {
        _fx.GoToTab("nav-system-health");
        Assert.NotNull(_fx.FindButtonById("btn-system-health-memory-errors"));
    }

    [Fact]
    public void SystemHealth_RunMemTestButton_Exists()
    {
        _fx.GoToTab("nav-system-health");
        Assert.NotNull(_fx.FindButtonById("btn-system-health-memtest"));
    }

    // ---------------- App updates ----------------

    [Fact]
    public void AppUpdates_ScanButton_Exists()
    {
        _fx.GoToTab("nav-app-updates");
        Assert.NotNull(_fx.FindButtonById("btn-app-updates-scan"));
    }
}
