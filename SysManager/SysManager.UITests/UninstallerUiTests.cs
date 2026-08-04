// SysManager · UninstallerUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.UITests;

[Collection("App")]
public class UninstallerUiTests
{
    private readonly AppFixture _fixture;

    public UninstallerUiTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public void CurrentSession_ShowsMatchingGuidanceWithoutAdminRelaunchButton()
    {
        _fixture.GoToTab("nav-uninstaller");

        Assert.NotNull(_fixture.FindButtonById("btn-uninstaller-uninstall-selected"));
        Assert.False(_fixture.HasButtonWithName("Run as administrator"));
        Assert.False(_fixture.HasButtonWithName("Relaunch as administrator"));

        var expectedGuidance = Helpers.AdminHelper.IsElevated()
            ? "Uninstall is disabled in administrator sessions. Reopen SysManager normally to continue."
            : "Uninstallers request administrator access themselves when needed.";
        Assert.True(
            _fixture.HasText(expectedGuidance),
            "The Uninstaller did not show the guidance for the current integrity level.");
    }
}
