// SysManager · SmokeUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using FlaUI.Core.AutomationElements;

namespace SysManager.UITests;

/// <summary>
/// Smoke: every tab navigates and its signature control renders.
/// </summary>
[Collection("App")]
public class SmokeUiTests
{
    private readonly AppFixture _fx;
    public SmokeUiTests(AppFixture fx) => _fx = fx;

    [Fact]
    public void MainWindow_HasExpectedTitle()
    {
        Assert.Contains("SysManager", _fx.MainWindow.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NavTree_IsReachable_ByAutomationId()
    {
        // Verify at least one nav item is reachable in the new tree layout.
        Assert.NotNull(_fx.FindById("nav-dashboard"));
    }

    [Fact]
    public void ContentHost_RendersCurrentTab()
    {
        // Indirect check: the content area shows whatever view is associated
        // with the selected nav item. Navigate to Logs and ensure its header
        // appears somewhere inside the window.
        _fx.GoToTab("nav-logs");
        Assert.NotNull(_fx.WaitForText("System logs"));
    }

    [Theory]
    [InlineData("nav-dashboard", "Scan system")]
    [InlineData("nav-app-updates", "Scan for updates")]
    [InlineData("nav-windows-update", "Windows Update")]
    [InlineData("nav-system-health", "Overview")]
    [InlineData("nav-cleanup", "Clean TEMP")]
    [InlineData("nav-ping", "Targets")]
    [InlineData("nav-drivers", "List drivers")]
    [InlineData("nav-logs", "System logs")]
    public void EachTab_ShowsSignatureElement(string navId, string expectedText)
    {
        _fx.GoToTab(navId);
        Assert.NotNull(_fx.WaitForText(expectedText));
    }

    [Fact]
    public void AllNavItems_HaveExpectedAutomationIds()
    {
        foreach (var id in new[] {
            "nav-dashboard", "nav-app-updates", "nav-windows-update",
            "nav-system-health", "nav-cleanup", "nav-ping",
            "nav-drivers", "nav-logs" })
        {
            _ = FindSingleById(id);
        }
    }

    [Fact]
    public void NavSelection_PersistsAfterChange()
    {
        _fx.GoToTab("nav-ping");
        // After clicking the Network → Ping tab, verify the content area shows its content.
        Assert.NotNull(_fx.WaitForText("Targets"));
    }

    [Fact]
    public void NavSelection_ExposesCurrentItemStatus()
    {
        _fx.GoToTab("nav-dashboard");
        var dashboard = FindSingleById("nav-dashboard");
        Assert.Equal("Selected", dashboard.ItemStatus);

        _fx.GoToTab("nav-logs");
        var logs = FindSingleById("nav-logs");
        Assert.Equal("Selected", logs.ItemStatus);
        Assert.NotEqual("Selected", dashboard.ItemStatus);

        _fx.GoToTab("nav-ping");
        var ping = FindSingleById("nav-ping");
        Assert.Equal("Selected", ping.ItemStatus);
        Assert.NotEqual("Selected", logs.ItemStatus);
    }

    private AutomationElement FindSingleById(string automationId)
    {
        var matches = _fx.MainWindow.FindAllDescendants(
            condition => condition.ByAutomationId(automationId));
        Assert.Single(matches);
        return matches[0];
    }

    [Fact]
    public void Navigate_BackAndForth_NoCrash()
    {
        _fx.GoToTab("nav-logs");
        _fx.GoToTab("nav-ping");
        _fx.GoToTab("nav-dashboard");
        _fx.GoToTab("nav-cleanup");
        _fx.GoToTab("nav-logs");
        Assert.NotNull(_fx.WaitForText("System logs"));
    }
}
