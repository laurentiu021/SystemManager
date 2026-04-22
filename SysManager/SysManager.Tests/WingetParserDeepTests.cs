// SysManager · WingetParserDeepTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class WingetParserDeepTests
{
    private static List<AppPackage> Parse(List<string> lines)
    {
        var m = typeof(WingetService).GetMethod("ParseUpgradeTable", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<AppPackage>)m.Invoke(null, new object[] { lines })!;
    }

    [Fact]
    public void HandlesMixedCaseHeader()
    {
        var lines = new List<string>
        {
            "name                         id                             version   available   source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
        };
        var result = Parse(lines);
        // Header is case-insensitive.
        Assert.Single(result);
    }

    [Fact]
    public void HandlesPackageWithSpacesInName()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Visual Studio Build Tools    Microsoft.VCBuildTools         17.10.0   17.11.0     winget",
        };
        var result = Parse(lines);
        Assert.Single(result);
        Assert.Equal("Visual Studio Build Tools", result[0].Name);
        Assert.Equal("Microsoft.VCBuildTools", result[0].Id);
    }

    [Fact]
    public void HandlesMultiLineTableWithSpecialChars()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Python 3.12 (3.12.4)         Python.Python.3.12             3.12.4    3.12.5      winget",
            "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget",
        };
        var result = Parse(lines);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void HandlesNoSourceColumn()
    {
        // Some winget versions omit the Source column.
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available",
            "---------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0",
        };
        var result = Parse(lines);
        Assert.Single(result);
        Assert.Equal("winget", result[0].Source);
    }

    [Fact]
    public void NoUpgradesAvailable_ReturnsEmpty()
    {
        var lines = new List<string>
        {
            "No applicable upgrades found.",
            "No installed package found matching input criteria.",
        };
        var result = Parse(lines);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "  Padded Name                Padded.Id                      1.0.0     2.0.0       winget   ",
        };
        var result = Parse(lines);
        Assert.Equal("Padded Name", result[0].Name);
        Assert.Equal("Padded.Id", result[0].Id);
        Assert.Equal("winget", result[0].Source);
    }

    [Fact]
    public void Parse_IgnoresShortLines()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "abc",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
        };
        var result = Parse(lines);
        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
    }

    [Fact]
    public void Parse_StopsAtSingularPackageSummary()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "1 package has version numbers that cannot be determined.",
        };
        var result = Parse(lines);
        Assert.Single(result);
    }

    [Fact]
    public void Parse_HandlesAllEmptyInput_Safely()
    {
        var result = Parse(new List<string> { "" });
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_HandlesVeryLongId_WithoutTruncation()
    {
        var lines = new List<string>
        {
            "Name                         Id                                                                   Version   Available   Source",
            "----------------------------------------------------------------------------------------------------------------------------------",
            "Some App                     Super.Long.Package.Identifier.That.Goes.On.And.On                    1.0.0     2.0.0       winget",
        };
        var result = Parse(lines);
        Assert.Single(result);
        Assert.Contains("Super.Long.Package", result[0].Id);
    }
}
