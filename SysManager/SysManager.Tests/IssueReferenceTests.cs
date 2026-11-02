// SysManager · IssueReferenceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;

namespace SysManager.Tests;

/// <summary>
/// The code, the tests, the scripts and the documents cite a change by its issue or pull request number, not by a
/// review's own label for it.
/// </summary>
/// <remarks>
/// Comments tagged changes with labels from review notes that never reached the repository: SEC-003, PERF-008,
/// CQ-M2, "audit finding tests #12", "P2 #43", Audit-2. A reader cannot look any of them up, SEC-003 and SEC-004
/// each marked two unrelated changes, and the numbered ones read as links to unrelated issues: #12, cited for the
/// App Blocker registry tests, asks for webcam and microphone alerts. Each now names the pull request it came from
/// (#2745).
/// <para>CHANGELOG.md is left out: its entries say what the release notes of their time said, labels included.
/// This file has to name the labels it keeps out, so the sweep skips it too.</para>
/// </remarks>
public partial class IssueReferenceTests
{
    private const string ThisFile = nameof(IssueReferenceTests) + ".cs";

    [Fact]
    public void EveryTextFile_CitesChangesByIssueOrPullRequest()
    {
        var root = TestPaths.RepoRoot();
        var files = TestPaths.RepositoryTextFiles()
            .Where(p => Path.GetFileName(p) is not (ThisFile or "CHANGELOG.md"))
            .ToList();

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (ReviewLabel().IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}  {lines[i].Trim()}");
            }
        }

        // Vacuity floor: the same files the inclusive-terms sweep reads, over 900 of them. A file from each place
        // the labels were found in is named as well, so losing one cannot hide inside the total.
        Assert.True(files.Count >= 900,
            $"only {files.Count} files were read, so the sweep is not seeing the repository");
        string[] known =
        [
            Path.Combine(".github", "workflows", "auto-release.yml"),
            Path.Combine("SysManager", "SysManager", "App.xaml"),
            Path.Combine("SysManager", "SysManager", "Services", "UninstallerService.cs"),
            Path.Combine("SysManager", "SysManager.Tests", "GamingProfileServiceTests.cs"),
            Path.Combine("SysManager", "SysManager.IntegrationTests", "DeepCleanupViewModelTests.cs"),
        ];
        foreach (var path in known)
            Assert.Contains(Path.Combine(root, path), files);

        Assert.True(offenders.Count == 0,
            "cite the issue or pull request the change came from instead:\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("// SEC-M7: Shell metacharacters are rejected to prevent command injection.", true)]
    [InlineData("// SEC-003: validate drive letter format to prevent argument injection", true)]
    [InlineData("(PERF-008-safe on repeated cards). This is the revamp's signature", true)]
    [InlineData("/// <para><b>Security note (SEC-005 / SEC-M8):</b> ExecutionPolicy is set to Bypass", true)]
    [InlineData("// ── SEC-LPE: user-writable per-user location is trusted only when NOT elevated ──", true)]
    [InlineData("# CI-001: Check for breaking changes (! suffix) before minor/patch", true)]
    [InlineData("// QA-005 fix: clamped to 100 max", true)]
    [InlineData("/// (audit finding tests #12). The service takes an injectable registry root", true)]
    [InlineData("// ---------- irreversible-shred confirmation gate (audit tests #2) ----------", true)]
    [InlineData("// ---------- select-all header checkbox (audit #66) ----------", true)]
    [InlineData("// Regression (P2 #43): scanned categories arrive PRE-SELECTED", true)]
    [InlineData("// ── Audit-1 fix: crash-recovery replays ONLY what actually applied ─────", true)]
    [InlineData("// dragging=false + focused=true is the Audit #3 regression case", true)]
    [InlineData("// ---------- symlink bypass regression (P0) ----------", true)]
    [InlineData("// The P1, from the user's side.", true)]
    [InlineData("// Regression (#1412): scanned categories arrive PRE-SELECTED", false)]
    [InlineData("// Clamped to 100 at most (#252)", false)]
    [InlineData("TEST-NET-1 (192.0.2.0/24) is reserved for documentation", false)]
    [InlineData("![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)", false)]
    [InlineData("MIT, BleachBit is GPL-3.0", false)]
    [InlineData("File.WriteAllText(target, \"BUILD-1\");", false)]
    [InlineData("Assert.Equal(\"Hottest Core (P-Core #2)\", readings[1].SensorName);", false)]
    [InlineData("the SHA-256 of the download, in UTF-8 without a BOM", false)]
    [InlineData("var debug = \"DEBUG-1\";", false)]
    public void TheCheck_FindsReviewLabels_AndLeavesOtherNumbersAlone(string line, bool flagged)
        => Assert.Equal(flagged, ReviewLabel().IsMatch(line));

    /// <summary>
    /// A review's label for a change: a category and an item (SEC-003, PERF-M4, CQ-M2, SEC-LPE), an audit item
    /// ("audit #66", Audit-2, "audit finding tests #12"), or a priority ("P2 #43", "(P0)", "The P1").
    /// </summary>
    [GeneratedRegex(@"\b(?:SEC|PERF|CQ|QA|CI|FUNC|MEM|LEAK|BUG|THR|BIND)-(?:[A-Z]+\d*|\d+)\b|\bTEST-\d+\b"
        + @"|[Aa]udit(?: finding)?(?: tests)?[- ]#?\d+|\bP[0-3] #\d+|\(P[0-3]\)|\bThe P[0-3]\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReviewLabel();
}
