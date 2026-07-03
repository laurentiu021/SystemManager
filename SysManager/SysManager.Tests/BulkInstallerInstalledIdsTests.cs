// SysManager · BulkInstallerInstalledIdsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pins <see cref="BulkInstallerViewModel.ParseInstalledIds"/>, which decides the "Installed"
/// badge from <c>winget list</c> output. The old code scanned each raw row with
/// <c>line.Contains(id)</c>, so one curated Id that is a substring of an installed one (e.g.
/// <c>Microsoft.Teams</c> ⊂ <c>Microsoft.Teams.Classic</c>) was falsely flagged installed.
/// The parser now extracts exact Ids; detection is a set membership test.
/// </summary>
public class BulkInstallerInstalledIdsTests
{
    // winget list fixed-width layout: Name | Id | Version | Available | Source.
    private const int IdCol = 30;
    private const int VersionCol = 62;
    private const int AvailableCol = 82;
    private const int SourceCol = 96;

    private static string Row(string name, string id, string version, string available = "", string source = "winget")
    {
        var sb = new StringBuilder();
        sb.Append(name.PadRight(IdCol));
        sb.Append(id.PadRight(VersionCol - IdCol));
        sb.Append(version.PadRight(AvailableCol - VersionCol));
        sb.Append(available.PadRight(SourceCol - AvailableCol));
        sb.Append(source);
        return sb.ToString();
    }

    private static string Table(params string[] rows)
    {
        var sb = new StringBuilder();
        sb.Append("Name".PadRight(IdCol));
        sb.Append("Id".PadRight(VersionCol - IdCol));
        sb.Append("Version".PadRight(AvailableCol - VersionCol));
        sb.Append("Available".PadRight(SourceCol - AvailableCol));
        sb.AppendLine("Source");
        sb.AppendLine(new string('-', 110));
        foreach (var r in rows) sb.AppendLine(r);
        sb.AppendLine($"{rows.Length} packages have available upgrades.");
        return sb.ToString();
    }

    [Fact]
    public void ParseInstalledIds_ExtractsExactIds()
    {
        var output = Table(
            Row("Microsoft Teams classic", "Microsoft.Teams.Classic", "1.6.0"),
            Row("7-Zip", "7zip.7zip", "23.01"));

        var ids = BulkInstallerViewModel.ParseInstalledIds(output);

        Assert.Contains("Microsoft.Teams.Classic", ids);
        Assert.Contains("7zip.7zip", ids);
    }

    [Fact]
    public void ParseInstalledIds_SubstringId_NotFalselyMatched()
    {
        // Regression (F20): only "Microsoft.Teams.Classic" is installed. A curated app whose Id
        // is the *substring* "Microsoft.Teams" must NOT be reported installed — the old
        // Contains() scan over the raw row flagged it.
        var output = Table(Row("Microsoft Teams classic", "Microsoft.Teams.Classic", "1.6.0"));

        var ids = BulkInstallerViewModel.ParseInstalledIds(output);

        Assert.Contains("Microsoft.Teams.Classic", ids);
        Assert.DoesNotContain("Microsoft.Teams", ids);
    }

    [Fact]
    public void ParseInstalledIds_IsCaseInsensitive()
    {
        var output = Table(Row("Mozilla Firefox", "Mozilla.Firefox", "128.0"));

        var ids = BulkInstallerViewModel.ParseInstalledIds(output);

        // The set uses OrdinalIgnoreCase so a curated Id differing only in case still matches.
        Assert.Contains("mozilla.firefox", ids);
    }

    [Fact]
    public void ParseInstalledIds_EmptyOutput_ReturnsEmpty() =>
        Assert.Empty(BulkInstallerViewModel.ParseInstalledIds(string.Empty));

    [Fact]
    public void ParseInstalledIds_HeaderOnly_ReturnsEmpty() =>
        Assert.Empty(BulkInstallerViewModel.ParseInstalledIds(Table()));
}
