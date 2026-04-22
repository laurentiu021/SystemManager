// SysManager · DashboardTabUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.UITests;

[Collection("App")]
public class DashboardTabUiTests
{
    private readonly AppFixture _fx;
    public DashboardTabUiTests(AppFixture fx) => _fx = fx;

    private void GoTo() => _fx.GoToTab("nav-dashboard");

    [Fact]
    public void ScanSystemButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButton("Scan system"));
    }

    [Fact]
    public void SectionLabels_Present()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Operating System"));
        Assert.NotNull(_fx.WaitForText("CPU"));
        Assert.NotNull(_fx.WaitForText("Memory"));
        Assert.NotNull(_fx.WaitForText("Storage"));
    }
}
