// SysManager · DashboardTabUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
        // The live-vitals tiles: CPU / MEMORY / GPU, plus the Storage section.
        // (WaitForText is case-insensitive, so "Memory" matches the "MEMORY" tile.)
        Assert.NotNull(_fx.WaitForText("CPU"));
        Assert.NotNull(_fx.WaitForText("Memory"));
        Assert.NotNull(_fx.WaitForText("Storage"));
    }
}
