// SysManager · BulkInstallerSearchParseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pins <see cref="BulkInstallerViewModel.ParseSearchResults"/>, which routes
/// <c>winget search</c> output through the shared column parser instead of the
/// old hand-rolled character-offset slicing. The parser locates columns by the
/// header word positions, so the fixtures here are built from explicit column
/// widths — Id/Version values are guaranteed to line up under the "Id"/"Version"
/// header words exactly as winget's fixed-width output does.
/// </summary>
public class BulkInstallerSearchParseTests
{
    // Column start offsets, matching winget search's fixed-width layout.
    private const int IdCol = 29;
    private const int VersionCol = 60;

    /// <summary>Builds one fixed-width row with values under the correct columns.</summary>
    private static string Row(string name, string id, string version)
    {
        var sb = new StringBuilder();
        sb.Append(name.PadRight(IdCol));
        sb.Append(id.PadRight(VersionCol - IdCol));
        sb.Append(version);
        return sb.ToString();
    }

    private static string Table(params string[] rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Row("Name", "Id", "Version") + "   Match       Source");
        sb.AppendLine(new string('-', 90));
        foreach (var r in rows) sb.AppendLine(r);
        return sb.ToString();
    }

    [Fact]
    public void ParseSearchResults_ExtractsNameAndId()
    {
        var output = Table(
            Row("Visual Studio", "Microsoft.VisualStudio", "17.9.0"),
            Row("7-Zip", "7zip.7zip", "23.01"));

        var apps = BulkInstallerViewModel.ParseSearchResults(output);

        Assert.Equal(2, apps.Count);
        Assert.Equal("Visual Studio", apps[0].Name);
        Assert.Equal("Microsoft.VisualStudio", apps[0].WingetId);
        Assert.Equal("7-Zip", apps[1].Name);
        Assert.Equal("7zip.7zip", apps[1].WingetId);
    }

    [Fact]
    public void ParseSearchResults_TagsEveryRowAsCustomCategory()
    {
        var output = Table(Row("Firefox", "Mozilla.Firefox", "128.0"));
        var apps = BulkInstallerViewModel.ParseSearchResults(output);

        Assert.NotEmpty(apps);
        Assert.All(apps, a => Assert.Equal("Custom", a.Category));
    }

    [Fact]
    public void ParseSearchResults_EmptyOutput_ReturnsEmpty() =>
        Assert.Empty(BulkInstallerViewModel.ParseSearchResults(string.Empty));

    [Fact]
    public void ParseSearchResults_HeaderOnly_ReturnsEmpty() =>
        Assert.Empty(BulkInstallerViewModel.ParseSearchResults(Table()));

    [Fact]
    public void ParseSearchResults_CapsAtThirtyRows()
    {
        var rows = new string[50];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = Row($"App{i}", $"Publisher.App{i}", "1.0.0");

        var apps = BulkInstallerViewModel.ParseSearchResults(Table(rows));
        Assert.Equal(30, apps.Count);
    }
}
