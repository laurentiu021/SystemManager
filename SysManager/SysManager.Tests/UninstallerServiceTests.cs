// SysManager · UninstallerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UninstallerService"/>. Focuses on the table parser
/// since winget calls are integration-level.
/// </summary>
public class UninstallerServiceTests
{
    // ── ParseListTable ──

    [Fact]
    public void ParseListTable_EmptyInput_ReturnsEmpty()
    {
        var result = UninstallerService.ParseListTable(new List<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void ParseListTable_NoHeader_ReturnsEmpty()
    {
        var lines = new List<string> { "some random text", "another line" };
        var result = UninstallerService.ParseListTable(lines);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseListTable_ValidTable_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Name                         Id                           Version    Source",
            "-------------------------------------------------------------------------",
            "Visual Studio Code           Microsoft.VisualStudioCode   1.90.0     winget",
            "Git                          Git.Git                      2.45.1     winget",
            "2 packages installed."
        };

        var result = UninstallerService.ParseListTable(lines);

        Assert.Equal(2, result.Count);
        Assert.Equal("Visual Studio Code", result[0].Name);
        Assert.Equal("Microsoft.VisualStudioCode", result[0].Id);
        Assert.Equal("1.90.0", result[0].Version);
        Assert.Equal("winget", result[0].Source);
        Assert.Equal("Git", result[1].Name);
        Assert.Equal("Git.Git", result[1].Id);
    }

    [Fact]
    public void ParseListTable_WithAvailableColumn_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Name              Id                  Version   Available  Source",
            "----------------------------------------------------------------",
            "Node.js           OpenJS.NodeJS       20.11.0   20.12.0    winget",
        };

        var result = UninstallerService.ParseListTable(lines);

        Assert.Single(result);
        Assert.Equal("Node.js", result[0].Name);
        Assert.Equal("OpenJS.NodeJS", result[0].Id);
        Assert.Equal("20.11.0", result[0].Version);
    }

    [Fact]
    public void ParseListTable_SkipsSeparatorLines()
    {
        var lines = new List<string>
        {
            "Name              Id                  Version   Source",
            "------------------------------------------------------",
            "--some separator--",
            "App               Some.App            1.0       winget",
        };

        var result = UninstallerService.ParseListTable(lines);
        Assert.Single(result);
        Assert.Equal("Some.App", result[0].Id);
    }

    [Fact]
    public void ParseListTable_StopsAtSummaryLine()
    {
        var lines = new List<string>
        {
            "Name              Id                  Version   Source",
            "------------------------------------------------------",
            "App1              Some.App1            1.0       winget",
            "App2              Some.App2            2.0       winget",
            "5 packages installed.",
            "App3              Some.App3            3.0       winget",
        };

        var result = UninstallerService.ParseListTable(lines);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseListTable_SkipsShortLines()
    {
        var lines = new List<string>
        {
            "Name              Id                  Version   Source",
            "------------------------------------------------------",
            "X",
            "App               Some.App            1.0       winget",
        };

        var result = UninstallerService.ParseListTable(lines);
        Assert.Single(result);
    }

    // ── InstalledApp model ──

    [Fact]
    public void InstalledApp_DefaultValues()
    {
        var app = new InstalledApp();
        Assert.False(app.IsSelected);
        Assert.Equal("", app.Name);
        Assert.Equal("", app.Id);
        Assert.Equal("", app.Version);
        Assert.Equal("", app.Source);
        Assert.Equal("", app.Status);
    }

    [Fact]
    public void InstalledApp_PropertyChange_Notifies()
    {
        var app = new InstalledApp();
        var changed = new List<string>();
        app.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        app.IsSelected = true;
        app.Name = "Test";
        app.Id = "test.id";
        app.Version = "1.0";
        app.Status = "Removed";

        Assert.Contains("IsSelected", changed);
        Assert.Contains("Name", changed);
        Assert.Contains("Id", changed);
        Assert.Contains("Version", changed);
        Assert.Contains("Status", changed);
    }
}
