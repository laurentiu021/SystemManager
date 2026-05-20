// SysManager · WingetTableParserTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
using SysManager.Helpers;

namespace SysManager.Tests;

public class WingetTableParserTests
{
    private static readonly Regex HeaderPattern = new(@"^\s*Name\s+Id\s+Version", RegexOptions.IgnoreCase);
    private static readonly Regex SummaryPattern = new(@"^\d+\s+packages?\s+", RegexOptions.IgnoreCase);

    [Fact]
    public void Parse_EmptyLines_ReturnsEmpty()
    {
        var lines = new List<string>();
        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NoHeader_ReturnsEmpty()
    {
        var lines = new List<string>
        {
            "Some random output",
            "No applicable upgrades found.",
            "Another line without headers"
        };
        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_HeaderOnly_ReturnsEmpty()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------"
        };
        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ValidTable_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        Assert.Equal(3, result.Count);

        Assert.Equal("Git", result[0].Name);
        Assert.Equal("Git.Git", result[0].Id);
        Assert.Equal("2.47.0", result[0].Version);
        Assert.Equal("2.48.0", result[0].Available);
        Assert.Equal("winget", result[0].Source);

        Assert.Equal("PowerShell", result[1].Name);
        Assert.Equal("Microsoft.PowerShell", result[1].Id);

        Assert.Equal("Node.js", result[2].Name);
        Assert.Equal("OpenJS.NodeJS", result[2].Id);
    }

    [Fact]
    public void Parse_ShortLines_SkipsGracefully()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "ab",
            "x",
            "Git                          Git.Git                        2.47.0    2.48.0      winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
    }

    [Fact]
    public void Parse_StopsAtSummaryLine()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "3 packages have version numbers that cannot be determined.",
            "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        // Should stop at the summary line and not include PowerShell
        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
    }
}
