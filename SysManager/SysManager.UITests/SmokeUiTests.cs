// SysManager · SmokeUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
    public void NavList_IsReachable_ByAutomationId()
    {
        Assert.NotNull(_fx.FindById("NavList"));
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
    [InlineData("nav-dashboard",      "Scan system")]
    [InlineData("nav-app-updates",    "Scan for updates")]
    [InlineData("nav-windows-update", "Check module")]
    [InlineData("nav-system-health",  "Overview")]
    [InlineData("nav-cleanup",        "Clean TEMP")]
    [InlineData("nav-network",        "Targets")]
    [InlineData("nav-drivers",        "List installed drivers")]
    [InlineData("nav-logs",           "System logs")]
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
            "nav-system-health", "nav-cleanup", "nav-network",
            "nav-drivers", "nav-logs" })
        {
            Assert.NotNull(_fx.FindById(id));
        }
    }

    [Fact]
    public void NavList_Selection_PersistsAfterChange()
    {
        _fx.GoToTab("nav-network");
        var navList = _fx.FindById("NavList")!.AsListBox();
        var selectedItem = navList.SelectedItem;
        Assert.NotNull(selectedItem);
        Assert.Equal("Network", selectedItem!.Name);
    }

    [Fact]
    public void Navigate_BackAndForth_NoCrash()
    {
        _fx.GoToTab("nav-logs");
        _fx.GoToTab("nav-network");
        _fx.GoToTab("nav-dashboard");
        _fx.GoToTab("nav-cleanup");
        _fx.GoToTab("nav-logs");
        Assert.NotNull(_fx.WaitForText("System logs"));
    }
}
