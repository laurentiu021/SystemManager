// SysManager · UninstallerUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.UITests;

[Collection("App")]
public class UninstallerUiTests
{
    private readonly AppFixture _fixture;

    public UninstallerUiTests(AppFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The Uninstaller shows the guidance that matches the session's integrity level, and never offers to
    /// relaunch elevated — this is the one tab where elevation takes capability away.
    /// </summary>
    /// <remarks>
    /// The expected wording is a FRAGMENT of each banner, not the whole sentence. Asserting the full
    /// sentence is what let this test rot: PR #1808 rewrote the elevated banner's copy and left the
    /// assertion quoting the old text, which then existed nowhere in the app. That branch only runs when
    /// the test session is elevated — as it is on the CI runner — and the UI job is
    /// <c>continue-on-error</c>, so it reported "pass" for weeks with this failing. A fragment survives
    /// rewording; <c>ArchitectureTests.EveryUiTextAssertion_QuotesCopyTheAppActuallyShips</c> is what
    /// catches the case where even the fragment stops matching.
    /// </remarks>
    [Fact]
    public void CurrentSession_ShowsMatchingGuidanceWithoutAdminRelaunchButton()
    {
        _fixture.GoToTab("nav-uninstaller");

        Assert.NotNull(_fixture.FindButtonById("btn-uninstaller-uninstall-selected"));
        Assert.False(_fixture.HasButtonWithName("Run as administrator"));
        Assert.False(_fixture.HasButtonWithName("Relaunch as administrator"));

        var elevated = Helpers.AdminHelper.IsElevated();
        var expectedGuidance = elevated
            ? "Uninstalling is turned off while SysManager runs as administrator"
            : "Uninstallers request administrator access themselves when needed";
        Assert.True(
            _fixture.HasText(expectedGuidance),
            $"The Uninstaller did not show the guidance for the current integrity level "
            + $"(elevated: {elevated}). Expected to find: \"{expectedGuidance}\".");
    }
}
