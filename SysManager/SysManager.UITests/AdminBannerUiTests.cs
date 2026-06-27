// SysManager · AdminBannerUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.UITests;

/// <summary>
/// Regression net for the elevation-banner uniformity work: every tab that
/// performs admin-gated actions must surface the shared "requires administrator"
/// banner when the app is NOT elevated. The UI test host runs the app
/// non-elevated, so the not-elevated banner variant is the one in view. If a
/// future refactor drops the banner from one of these tabs (as had happened to
/// six of them), the corresponding case fails by name.
/// </summary>
[Collection("App")]
public class AdminBannerUiTests
{
    private readonly AppFixture _fx;
    public AdminBannerUiTests(AppFixture fx) => _fx = fx;

    public static IEnumerable<object[]> PrivilegedTabs() => new[]
    {
        // Originally had the banner (reference implementations)
        new object[] { "nav-services" },
        new object[] { "nav-dns-hosts" },
        new object[] { "nav-uninstaller" },
        // Added by the uniformity fix (v1.43.0) — these regressed silently before
        new object[] { "nav-processes" },
        new object[] { "nav-startup" },
        new object[] { "nav-task-scheduler" },
        new object[] { "nav-defender-tweaks" },
        new object[] { "nav-file-lock" },
        new object[] { "nav-shortcut-cleaner" },
    };

    [Theory]
    [MemberData(nameof(PrivilegedTabs))]
    public void PrivilegedTab_ShowsAdminBanner_WhenNotElevated(string navId)
    {
        // Guard: this assertion is only meaningful when the test host is NOT
        // elevated (the banner's not-elevated variant is what carries the
        // "requires administrator" phrase). If the suite is ever run elevated,
        // skip rather than report a false failure.
        if (Helpers.AdminHelper.IsElevated())
            return;

        _fx.GoToTab(navId);
        Assert.True(
            _fx.HasAdminBanner(),
            $"Privileged tab '{navId}' did not show the 'requires administrator' banner when not elevated.");
    }
}
