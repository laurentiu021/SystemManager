// SysManager · AdminBannerUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.UITests;

/// <summary>
/// Regression net for the elevation-banner uniformity work: every tab that performs admin-gated actions
/// must surface an elevation banner, and the variant shown must match the session. If a future refactor
/// drops the banner from one of these tabs (as had happened to six of them), the corresponding case fails
/// by name.
/// <para>Which variant is in view depends on the host: the CI runner is elevated, a developer's box
/// usually is not, and the app inherits the test process's integrity level because <c>AppFixture</c>
/// launches it with <c>UseShellExecute = false</c>.</para>
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
        // Added by the uniformity fix (v1.43.0) — these regressed silently before
        new object[] { "nav-processes" },
        new object[] { "nav-startup" },
        new object[] { "nav-task-scheduler" },
        new object[] { "nav-defender-tweaks" },
        new object[] { "nav-file-lock" },
        new object[] { "nav-shortcut-cleaner" },
        // Gaming Profile (Preview) — freeing standby memory + pausing indexing need admin
        new object[] { "nav-gaming-profile" },
    };

    /// <summary>
    /// Each privileged tab shows the elevation banner that matches the session it is running in.
    /// </summary>
    /// <remarks>
    /// This used to open with <c>if (Helpers.AdminHelper.IsElevated()) return;</c> — a silent skip so an
    /// elevated session would not report a false failure. On the CI runner, which IS elevated, that meant
    /// all nine cases returned before reaching the tab: nine green rows asserting nothing. Every tab has
    /// both banner variants, so there is nothing to skip; asserting the one that belongs to the current
    /// integrity level covers both hosts, and it is the shape
    /// <c>UninstallerUiTests.CurrentSession_ShowsMatchingGuidanceWithoutAdminRelaunchButton</c> already uses.
    /// <para>The elevated variant carries "Running as administrator" in all nine views; only the clause
    /// after it differs per tab, so the shared prefix is what to wait for.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PrivilegedTabs))]
    public void PrivilegedTab_ShowsTheElevationBannerForTheCurrentSession(string navId)
    {
        _fx.GoToTab(navId);

        var elevated = Helpers.AdminHelper.IsElevated();
        var expected = elevated ? "Running as administrator" : "requires administrator";
        Assert.True(
            _fx.HasText(expected),
            $"Privileged tab '{navId}' showed no elevation banner for the current integrity level "
            + $"(elevated: {elevated}). Expected to find: \"{expected}\".");
    }
}
