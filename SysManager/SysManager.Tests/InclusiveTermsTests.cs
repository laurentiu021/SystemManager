// SysManager · InclusiveTermsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;

namespace SysManager.Tests;

/// <summary>
/// The code, the tests, the scripts and the documents use inclusive terms: main or primary, allowlist or denylist.
/// </summary>
/// <remarks>
/// Notification Blocker's main toggle was "the master switch" on screen, in its confirmation and in the README; the
/// same word ran through its comments, log messages, identifiers and test names, and the scheduler's fixed arguments
/// were "whitelisted" (#2617). The sweep reads every text file in the repository, not only the ones that were fixed,
/// so the words cannot come back somewhere else.
/// <para>Three uses stay, and they are the only ones allowed: the method names of the Windows audio interfaces
/// (<c>SetMasterVolume</c> and its relatives), the ledger file <c>notification-master-writes.json</c>, which a rename
/// would orphan on users' PCs, and winget-pkgs' own branch, in the link and the API calls that reach it. This file
/// has to name the words it keeps out, so it is the one file the sweep skips.</para>
/// </remarks>
public partial class InclusiveTermsTests
{
    private const string ThisFile = nameof(InclusiveTermsTests) + ".cs";

    [Fact]
    public void EveryTextFile_UsesInclusiveTerms()
    {
        var root = TestPaths.RepoRoot();
        var files = TextFiles(root);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (UsesAnOldTerm(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}  {lines[i].Trim()}");
            }
        }

        // Vacuity floor: 978 files were read when this was written. A file from each of the four places is named as
        // well, so losing one of the small places cannot hide inside the total.
        Assert.True(files.Count >= 900,
            $"only {files.Count} files were read, so the sweep is not seeing the repository");
        string[] known =
        [
            "README.md",
            Path.Combine(".github", "workflows", "release.yml"),
            Path.Combine("docs", "manual-smoke.ps1"),
            Path.Combine("SysManager", "SysManager", "Views", "NotificationBlockerView.xaml"),
        ];
        foreach (var path in known)
            Assert.Contains(Path.Combine(root, path), files);

        Assert.True(offenders.Count == 0,
            "use main or primary, and allowlist or denylist, instead:\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("partial void OnMasterEnabledChanged(bool value)", true)]
    [InlineData("public void CliArguments_MapToWhitelistedVerbs(MaintenanceAction action, string expected)", true)]
    [InlineData("// A BLACKLIST of folders", true)]
    [InlineData("a primary and a replica, not a master and a slave", true)]
    [InlineData("winget-pkgs keeps a master list", true)]
    [InlineData("see winget-pkgs/tree/master for the manifest, and the master switch", true)]
    [InlineData("try { any |= vol.SetMasterVolume(clamped, ref ctx) == 0; }", false)]
    [InlineData("internal const string WriteLedgerFileName = \"notification-master-writes.json\";", false)]
    [InlineData("gh api -X POST repos/laurentiu021/winget-pkgs/merge-upstream -f branch=master \\", false)]
    [InlineData("behind=$(gh api \"repos/microsoft/winget-pkgs/compare/master...laurentiu021:winget-pkgs:master\" \\",
        false)]
    [InlineData("[![winget](https://img.shields.io/badge/winget-laurentiu021.SysManager-0078D4?logo=windows)]"
        + "(https://github.com/microsoft/winget-pkgs/tree/master/manifests/l/laurentiu021/SysManager)", false)]
    [InlineData("The main switch. Off silences every notification, and the scheduler's arguments are an allowlist.",
        false)]
    public void TheCheck_FindsTheOldWords_AndLeavesTheKeptUsesAlone(string line, bool flagged)
        => Assert.Equal(flagged, UsesAnOldTerm(line));

    // A line uses an old word when one is still there after the kept uses are taken out of it.
    private static bool UsesAnOldTerm(string line)
        => OldTerm().IsMatch(line) && OldTerm().IsMatch(KeptUse().Replace(line, ""));

    // Every text file in the four places the repository's files live: its top folder, .github, docs and the
    // solution. Images and this file are left out.
    private static List<string> TextFiles(string root)
    {
        string[] places = [".github", "docs", "SysManager"];
        string[] images = [".png", ".gif", ".ico"];
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Concat(places.SelectMany(place => FilesBelow(Path.Combine(root, place))))
            .Where(p => !images.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => Path.GetFileName(p) != ThisFile)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    // The files below a place, without the folders the repository ignores there: build output, test results, and
    // the ones an editor keeps beside the solution (.vs, .idea). None of them is a file the repository publishes.
    private static IEnumerable<string> FilesBelow(string place)
        => Directory.EnumerateFiles(place, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(place, p)
                .Split(Path.DirectorySeparatorChar)
                .SkipLast(1)
                .Any(folder => folder.StartsWith('.')
                               || folder.Equals("bin", StringComparison.OrdinalIgnoreCase)
                               || folder.Equals("obj", StringComparison.OrdinalIgnoreCase)
                               || folder.Equals("TestResults", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The old words, inside identifiers too, so <c>OnMasterEnabledChanged</c> is found like prose.</summary>
    [GeneratedRegex(@"master|slave|whitelist|blacklist", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldTerm();

    /// <summary>
    /// The uses that stay: the audio interfaces' method names, the ledger file, and winget-pkgs' branch, which is the
    /// word right after a slash, colon or equals sign on a line that names winget-pkgs before it.
    /// </summary>
    [GeneratedRegex(@"MasterVolume|notification-master-writes|(?<=\bwinget-pkgs\b.*[/:=])master\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeptUse();
}
