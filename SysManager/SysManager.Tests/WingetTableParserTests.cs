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

    // ── Locale independence ───────────────────────────────────────────────
    // Regression for the CRIT bug: on a non-English Windows, winget translates the column
    // TITLES (only the words — the order and the dashes separator are identical), so the old
    // English `Name\s+Id\s+Version` header regex never matched and the App Updates / Uninstaller
    // / Bulk Installer tabs returned empty. The parser now locates the header via the dashes
    // separator and maps columns positionally, so these headers must parse without any
    // language-specific pattern. The caller pattern below deliberately does NOT match, forcing
    // the locale-agnostic fallback path.

    private static readonly Regex NeverMatches = new(@"(?!)");

    [Fact]
    public void Parse_GermanHeader_ParsesViaSeparatorFallback()
    {
        // de-DE: "Name Kennung Version Verfügbar Quelle" — the English regex can't match.
        var lines = new List<string>
        {
            "Name                         Kennung                        Version   Verfügbar   Quelle",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "PowerShell                   Microsoft.PowerShell           7.4.3.0   7.4.4.0     winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Equal(2, result.Count);
        Assert.Equal("Git", result[0].Name);
        Assert.Equal("Git.Git", result[0].Id);
        Assert.Equal("2.47.0", result[0].Version);
        Assert.Equal("2.48.0", result[0].Available);
        Assert.Equal("winget", result[0].Source);
        Assert.Equal("Microsoft.PowerShell", result[1].Id);
    }

    [Fact]
    public void Parse_FrenchHeader_ParsesViaSeparatorFallback()
    {
        // fr-FR: "Nom Id Version Disponible Source".
        var lines = new List<string>
        {
            "Nom                          Id                             Version   Disponible  Source",
            "-----------------------------------------------------------------------------------------",
            "Node.js                      OpenJS.NodeJS                  20.11.0   22.1.0      winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Node.js", result[0].Name);
        Assert.Equal("OpenJS.NodeJS", result[0].Id);
        Assert.Equal("20.11.0", result[0].Version);
        Assert.Equal("22.1.0", result[0].Available);
    }

    [Fact]
    public void Parse_EnglishHeader_StillParses_WhenPatternDoesNotMatch()
    {
        // Even the English table must parse through the separator fallback alone, proving the
        // positional column mapping is not secretly relying on the English header regex.
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Git.Git", result[0].Id);
        Assert.Equal("2.48.0", result[0].Available);
    }

    [Fact]
    public void Parse_NoHeaderAndNoSeparator_ReturnsEmpty()
    {
        // Neither the pattern nor a dashes row → nothing to anchor on → empty (no crash).
        var lines = new List<string> { "irgendeine Ausgabe", "keine Upgrades gefunden" };
        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_FourColumnListTable_MapsSourceNotAvailable()
    {
        // winget "list" has NO Available column: Name / Id / Version / Source (4 columns).
        // The 4th token is Source, not Available — mapping it to Available (a 5-column
        // assumption) would leave Source empty. Positional mapping must treat the last
        // column as Source when there are only 4.
        var lines = new List<string>
        {
            "Name                         Id                           Version    Source",
            "-------------------------------------------------------------------------",
            "Visual Studio Code           Microsoft.VisualStudioCode   1.90.0     winget"
        };

        var result = WingetTableParser.Parse(lines, HeaderPattern, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Microsoft.VisualStudioCode", result[0].Id);
        Assert.Equal("1.90.0", result[0].Version);
        Assert.Equal("winget", result[0].Source);   // must not be empty
        Assert.Equal("", result[0].Available);       // no Available column in a list table
    }

    [Fact]
    public void Parse_GermanFourColumnList_MapsSourceViaSeparatorFallback()
    {
        // de-DE "winget list" with the localized 4-column header (no Available) and the
        // English pattern forced off — the exact non-English list-tab scenario.
        var lines = new List<string>
        {
            "Name                         Kennung                      Version    Quelle",
            "-------------------------------------------------------------------------",
            "Visual Studio Code           Microsoft.VisualStudioCode   1.90.0     winget"
        };

        var result = WingetTableParser.Parse(lines, NeverMatches, SummaryPattern);

        Assert.Single(result);
        Assert.Equal("Microsoft.VisualStudioCode", result[0].Id);
        Assert.Equal("winget", result[0].Source);
        Assert.Equal("", result[0].Available);
    }
}
