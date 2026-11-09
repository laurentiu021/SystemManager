// SysManager · ThirdPartyNoticesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The notices of the open-source components the exe carries (#2856): that the exe carries them, and that they name
/// every package whose code is in it, under its license.
/// </summary>
/// <remarks>
/// <c>tools/ThirdPartyNotices.cs</c> writes the file from the restore graph. This reads the same graph on its own, the
/// way the build left it, so a dependency added or dropped without running the tool again fails here, in the pull
/// request, rather than reaching a release with notices that leave it out. Versions are not compared: the notices name
/// none, and a version bump that keeps a package's license needs no new notices.
/// </remarks>
public sealed class ThirdPartyNoticesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "smtpn_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Committed() =>
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), ThirdPartyNotices.FileName)).Replace("\r\n", "\n");

    /// <summary>The index at the top of the notices: each component's name and license id.</summary>
    private static Dictionary<string, string> Index(string notices)
    {
        var lines = notices.Split('\n');
        var start = Array.IndexOf(lines, "COMPONENTS");
        Assert.True(start > 0 && lines[start + 1].StartsWith("---", StringComparison.Ordinal),
            "the notices have no COMPONENTS index in the shape this test reads");
        Dictionary<string, string> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(start + 2).TakeWhile(l => l.Length > 0))
        {
            var colon = line.LastIndexOf(": ", StringComparison.Ordinal);
            index.Add(line[..colon], line[(colon + 2)..]);
        }
        return index;
    }

    /// <summary>
    /// Each package of the app's restore graph that brings the exe anything for win-x64, with its license id: the
    /// nuspec's expression, or the one <c>tools/licenses/packages.json</c> gives a package whose license is a file or a
    /// URL.
    /// </summary>
    private static Dictionary<string, string> ShippedPackages()
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.AppProject(), "obj",
            "project.assets.json")));
        using var kept = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tools", "licenses",
            "packages.json")));
        var packageFolder = assets.RootElement.GetProperty("packageFolders").EnumerateObject().First().Name;
        var graph = assets.RootElement.GetProperty("targets").EnumerateObject()
            .Single(t => !t.Name.Contains('/')).Value;

        Dictionary<string, string> shipped = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in graph.EnumerateObject())
        {
            if (entry.Value.GetProperty("type").GetString() != "package" || !Ships(entry.Value)) continue;
            var (id, version) = (entry.Name.Split('/')[0], entry.Name.Split('/')[1]);
            var nuspec = XDocument.Load(Path.Combine(packageFolder, id.ToLowerInvariant(), version.ToLowerInvariant(),
                id.ToLowerInvariant() + ".nuspec"));
            var license = nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == "license");
            shipped[id] = license?.Attribute("type")?.Value == "expression"
                ? license.Value.Trim()
                : kept.RootElement.TryGetProperty(id, out var over) && over.TryGetProperty("license", out var given)
                    ? given.GetString()!
                    : "(none: give it a license in tools/licenses/packages.json)";
        }
        return shipped;
    }

    private static bool Ships(JsonElement package)
    {
        foreach (var kind in new[] { "runtime", "native", "resource" })
        {
            if (package.TryGetProperty(kind, out var files)
                && files.EnumerateObject().Any(f => !f.Name.EndsWith("_._", StringComparison.Ordinal)))
                return true;
        }
        string[] windows = ["win-x64", "win", "any", "base"];
        return package.TryGetProperty("runtimeTargets", out var targets)
            && targets.EnumerateObject().Any(t => !t.Name.EndsWith("_._", StringComparison.Ordinal)
                && t.Value.TryGetProperty("rid", out var rid) && windows.Contains(rid.GetString()));
    }

    [Fact]
    public void EveryPackageTheExeCarries_IsInTheNotices_UnderItsLicense()
    {
        var shipped = ShippedPackages();
        var index = Index(Committed());
        var packages = index.Where(e => !e.Key.Contains(" (", StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

        // A floor, so a graph read as empty cannot pass by agreeing with an index read as empty.
        Assert.True(shipped.Count >= 30, $"only {shipped.Count} packages read from the restore graph");

        var missing = shipped.Keys.Except(packages.Keys, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var gone = packages.Keys.Except(shipped.Keys, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var changed = shipped.Where(s => packages.TryGetValue(s.Key, out var named) && named != s.Value)
            .Select(s => $"{s.Key}: {packages[s.Key]} in the notices, {s.Value} now").ToList();
        Assert.True(missing.Count == 0 && gone.Count == 0 && changed.Count == 0,
            "THIRD-PARTY-NOTICES.txt no longer matches the packages the exe carries. Run "
            + "`dotnet run tools/ThirdPartyNotices.cs` from the repository root and commit what it writes.\n"
            + (missing.Count > 0 ? "  not in the notices: " + string.Join(", ", missing) + "\n" : "")
            + (gone.Count > 0 ? "  no longer in the exe: " + string.Join(", ", gone) + "\n" : "")
            + (changed.Count > 0 ? "  license changed: " + string.Join("; ", changed) + "\n" : ""));
    }

    [Theory]
    [InlineData(".NET runtime (Microsoft.NETCore.App)")]
    [InlineData("Windows Desktop runtime (Microsoft.WindowsDesktop.App)")]
    public void TheNotices_NameTheRuntimeTheExeIsPublishedWith(string runtime)
    {
        var notices = Committed();

        Assert.Equal("MIT", Index(notices)[runtime]);
        Assert.Contains("\n" + runtime + "\n", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLicenseTheNoticesReferTo_HasItsTextInThem()
    {
        const string Referral = "whose text is under LICENSE TEXTS at the end of this file.";
        var notices = Committed();
        var referred = notices.Split('\n')
            .Where(l => l.StartsWith("License: ", StringComparison.Ordinal) && l.EndsWith(Referral, StringComparison.Ordinal))
            .Select(l => l["License: ".Length..l.IndexOf(", whose", StringComparison.Ordinal)])
            .Distinct()
            .ToList();
        // The GNU LGPL builds on the GNU GPL, so a component under the one needs the other's text too.
        if (Index(notices).Values.Any(v => v.StartsWith("LGPL", StringComparison.Ordinal)))
            referred.Add("GNU General Public License 3.0 (GPL-3.0-only)");

        Assert.NotEmpty(referred);
        var texts = notices[notices.IndexOf("\nLICENSE TEXTS\n", StringComparison.Ordinal)..];
        foreach (var license in referred) Assert.Contains("--- " + license + " ---", texts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExe_CarriesTheCommittedNotices()
    {
        var carried = ThirdPartyNotices.Read();

        Assert.NotNull(carried);
        Assert.Equal(Committed(), carried!.Replace("\r\n", "\n"));
    }

    [Fact]
    public void WriteTo_WritesTheNoticesItIsGiven_AndReplacesAnEarlierCopy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ThirdPartyNotices.FileName), "an earlier copy, longer than the new one");

        var path = ThirdPartyNotices.WriteTo(_dir, "the notices");

        Assert.Equal(Path.Combine(_dir, ThirdPartyNotices.FileName), path);
        Assert.Equal("the notices", File.ReadAllText(path));
    }

    [Fact]
    public void WriteTo_MakesTheFolder_AndWritesWhatTheExeCarries()
    {
        var folder = Path.Combine(_dir, "not", "there");

        var path = ThirdPartyNotices.WriteTo(folder);

        Assert.Equal(ThirdPartyNotices.Read(), File.ReadAllText(path));
    }

    [Fact]
    public void WriteTo_WhereAFileStandsForTheFolder_Throws()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "a-file");
        File.WriteAllText(file, "");

        Assert.ThrowsAny<IOException>(() => ThirdPartyNotices.WriteTo(file, "the notices"));
    }
}
