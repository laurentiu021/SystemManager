// SysManager · WelcomeUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.UITests;

/// <summary>
/// The Welcome screen in the real app (#1635): a first launch opens on it, and "Just look around" gives the window back.
/// </summary>
/// <remarks>
/// The fixture closes the screen as the app starts, because every other test in the collection needs the sidebar it
/// disables, so this reads what the fixture saw. A machine whose profile already records the screen as seen does not
/// show it, and the test says so rather than passing: a CI runner is always a first launch.
/// </remarks>
[Collection("App")]
public class WelcomeUiTests
{
    private readonly AppFixture _fx;
    public WelcomeUiTests(AppFixture fx) => _fx = fx;

    [Fact]
    public void AFirstLaunch_OpensOnTheWelcomeScreen_AndJustLookAroundGivesTheWindowBack()
    {
        var shown = _fx.WelcomeAtLaunch;
        if (shown is null)
            Assert.Skip("This launch did not show the Welcome screen: the profile on this machine already records it as "
                        + "seen. Delete %LocalAppData%\\SysManager\\welcome.json to check it here; CI checks it on every run.");

        Assert.Equal("Welcome to SysManager", shown.AccessibleName);
        Assert.Equal(
            [
                "Check my PC's health — open System Health",
                "Clean up junk files — open Quick Cleanup",
                "Just look around — close this and stay on this page",
            ],
            shown.Buttons);
        Assert.True(shown.SidebarEnabledAfterClosing,
            "after \"Just look around\" the sidebar was still disabled, so no tab could be opened");
    }
}
