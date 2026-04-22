// SysManager · LogsTabUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using FlaUI.Core.AutomationElements;

namespace SysManager.UITests;

[Collection("App")]
public class LogsTabUiTests
{
    private readonly AppFixture _fx;
    public LogsTabUiTests(AppFixture fx) => _fx = fx;

    private void GoTo() => _fx.GoToTab("nav-logs");

    [Fact]
    public void Header_Visible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("System logs"));
    }

    [Fact]
    public void Subtitle_Visible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Windows Event Log"));
    }

    [Fact]
    public void SeverityPills_AllPresent()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Critical"));
        Assert.NotNull(_fx.WaitForText("Errors"));
        Assert.NotNull(_fx.WaitForText("Warnings"));
        Assert.NotNull(_fx.WaitForText("Info"));
    }

    [Fact]
    public void FilterCheckboxes_AllExist()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Verbose"));
        Assert.NotNull(_fx.WaitForText("Warning"));
        Assert.NotNull(_fx.WaitForText("Error"));
    }

    [Fact]
    public void RefreshButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButton("Refresh"));
    }

    [Fact]
    public void ExportCsvButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButton("Export CSV"));
    }

    [Fact]
    public void OpenEventViewerButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButton("Open Event Viewer"));
    }

    [Fact]
    public void OpenLogFolderButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButton("Open log folder"));
    }

    [Fact]
    public void EmptyStateMessage_Visible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Select an event"));
    }

    [Fact]
    public void LogDropdown_LabelVisible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Time"));
        Assert.NotNull(_fx.WaitForText("Max results"));
    }
}
