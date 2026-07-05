// SysManager · AllTabsSmokeUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.UITests;

/// <summary>
/// Breadth smoke coverage for EVERY navigable tab (all 58). For each tab this
/// asserts the app can navigate to it and the content area renders the tab's
/// expected page header — i.e. the view loaded rather than crashing or showing
/// a blank/wrong page. This is the regression net that catches a tab that stops
/// rendering after a refactor. Per-tab button/behaviour assertions live in the
/// dedicated *TabUiTests classes; this class is intentionally one assertion per
/// tab so a single broken tab is pinpointed by name.
/// </summary>
[Collection("App")]
public class AllTabsSmokeUiTests
{
    private readonly AppFixture _fx;
    public AllTabsSmokeUiTests(AppFixture fx) => _fx = fx;

    /// <summary>
    /// nav AutomationId → a substring of the page header that view renders.
    /// Substrings are used so an ampersand-encoded or suffixed title still matches
    /// (e.g. "DNS &amp; Hosts Editor" matches "DNS"). Placeholder/preview tabs show
    /// their nav label as the title.
    /// </summary>
    public static IEnumerable<object[]> AllTabs() => new[]
    {
        // ── System ──
        new object[] { "nav-dashboard", "Dashboard" },
        new object[] { "nav-system-health", "System Health" },
        new object[] { "nav-windows-update", "Windows Update" },
        new object[] { "nav-performance", "Performance Mode" },
        new object[] { "nav-services", "Services" },
        new object[] { "nav-startup", "Startup Manager" },
        new object[] { "nav-windows-features", "Windows Features" },
        new object[] { "nav-restore-points", "Restore Points" },
        new object[] { "nav-task-scheduler", "Task Scheduler" },
        new object[] { "nav-boot-analyzer", "Boot Analyzer" },
        new object[] { "nav-system-fixes", "System Fixes" },
        // ── Gaming & Profiles ──
        new object[] { "nav-gaming-profile", "Gaming Profile" },
        new object[] { "nav-standby-cleaner", "Standby List Cleaner" },
        new object[] { "nav-timer-resolution", "Timer Resolution" },
        new object[] { "nav-cpu-affinity", "CPU Core Affinity" },
        new object[] { "nav-display-profiles", "Display Profiles" },
        // ── Monitor ──
        new object[] { "nav-processes", "Process Manager" },
        new object[] { "nav-resource-history", "Resource History" },
        new object[] { "nav-privacy-monitor", "Privacy Monitor" },
        new object[] { "nav-app-alerts", "App Installation Alerts" },
        new object[] { "nav-file-lock", "File Lock Detector" },
        new object[] { "nav-settings-watchdog", "Settings Watchdog" },
        new object[] { "nav-bandwidth-monitor", "Bandwidth Monitor" },
        // ── Cleanup ──
        new object[] { "nav-cleanup", "Quick Cleanup" },
        new object[] { "nav-deep-cleanup", "Deep Cleanup" },
        new object[] { "nav-shortcut-cleaner", "Shortcut Cleaner" },
        new object[] { "nav-scheduled-maintenance", "Scheduled Maintenance" },
        // ── Storage ──
        new object[] { "nav-disk-analyzer", "Disk Analyzer" },
        new object[] { "nav-duplicates", "Duplicate Finder" },
        // ── Network ──
        new object[] { "nav-ping", "Ping" },
        new object[] { "nav-traceroute", "Traceroute" },
        new object[] { "nav-speed-test", "Speed Test" },
        new object[] { "nav-network-repair", "Network Repair" },
        new object[] { "nav-dns-hosts", "DNS" },
        // ── Apps ──
        new object[] { "nav-app-updates", "App updates" },
        new object[] { "nav-bulk-installer", "Bulk Installer" },
        new object[] { "nav-uninstaller", "Uninstaller" },
        // ── Privacy & Security ──
        new object[] { "nav-privacy-settings", "Privacy" },
        new object[] { "nav-file-shredder", "File Shredder" },
        new object[] { "nav-app-blocker", "Application Blocker" },
        new object[] { "nav-debloater", "Debloater" },
        new object[] { "nav-browser-cleaner", "Browser Cleaner" },
        new object[] { "nav-edge-onedrive", "Edge/OneDrive Remover" },
        new object[] { "nav-defender-tweaks", "Defender Tweaks" },
        new object[] { "nav-notification-blocker", "Notification Blocker" },
        // ── Customization ──
        new object[] { "nav-context-menu", "Context Menu" },
        new object[] { "nav-dark-mode", "Dark Mode Scheduler" },
        new object[] { "nav-volume-control", "Volume Control" },
        // ── Info ──
        new object[] { "nav-drivers", "Drivers" },
        new object[] { "nav-battery", "Battery Health" },
        new object[] { "nav-logs", "System Logs" },
        new object[] { "nav-system-report", "System Report" },
        new object[] { "nav-legacy-panels", "Legacy Panels" },
        new object[] { "nav-about", "About" },
        // ── Advanced ──
        new object[] { "nav-profile-export", "Profile Export" },
        new object[] { "nav-cli-interface", "CLI Interface" },
        new object[] { "nav-env-variables", "Environment Variables" },
    };

    [Theory]
    [MemberData(nameof(AllTabs))]
    public void Tab_Navigates_And_ShowsHeader(string navId, string expectedHeader)
    {
        _fx.GoToTab(navId);
        Assert.True(
            _fx.HasText(expectedHeader),
            $"Tab '{navId}' did not render its expected header '{expectedHeader}'.");
    }

    [Fact]
    public void AllTabs_HaveStableAutomationIds()
    {
        // Every nav id used above must be resolvable in the tree — a renamed or
        // dropped nav id is a navigation contract break and fails here loudly.
        foreach (var row in AllTabs())
            Assert.NotNull(_fx.FindById((string)row[0]));
    }
}
