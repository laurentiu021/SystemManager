// SysManager · UpdateServiceParseVersionBulkTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Bulk coverage for every realistic release tag shape we might see on
/// the GitHub releases page across the next several versions.
/// </summary>
public class UpdateServiceParseVersionBulkTests
{
    public static IEnumerable<object[]> ValidTags()
    {
        for (var major = 0; major <= 3; major++)
            for (var minor = 0; minor <= 9; minor++)
                for (var patch = 0; patch <= 5; patch++)
                {
                    yield return new object[] { $"v{major}.{minor}.{patch}", major, minor, patch };
                    yield return new object[] { $"{major}.{minor}.{patch}", major, minor, patch };
                }
    }

    [Theory]
    [MemberData(nameof(ValidTags))]
    public void ParseVersion_CoversAllCombinations(string tag, int major, int minor, int patch)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.Equal(major, v!.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Build);
    }

    public static IEnumerable<object[]> SuffixTags()
    {
        var suffixes = new[] { "-alpha", "-beta", "-rc1", "-rc.2", "-preview.3", "+build", "+meta.1" };
        foreach (var s in suffixes)
        {
            yield return new object[] { $"v0.5.0{s}", new Version(0, 5, 0) };
            yield return new object[] { $"1.2.3{s}", new Version(1, 2, 3) };
        }
    }

    [Theory]
    [MemberData(nameof(SuffixTags))]
    public void ParseVersion_StripsAllSuffixes(string tag, Version expected)
    {
        // Asserting only NotNull let any regression that mangles the strip but still leaves
        // something parseable pass: "v0.5.0-rc.2" collapsing to "0.5.02" becomes Version 0.5.2 and
        // would have gone unnoticed — on the comparison that decides whether the user is offered an
        // update.
        Assert.Equal(expected, UpdateService.ParseVersion(tag));
    }

    public static IEnumerable<object[]> Garbage()
    {
        var junk = new[] { "", "  ", "\t", "no", "abc", "v", "vv", "v1", "v1.x", "x.y.z", "-1.0.0",
                           "1.2.3.4.5.6", "v....", ".0.0", "v0..0", "0.0.", "1,2,3", "1:2:3" };
        foreach (var j in junk) yield return new object[] { j };
    }

    /// <summary>
    /// Every one of these must be REJECTED, not merely survived.
    /// <para>This test used to discard the result with <c>_ = v;</c> under a comment claiming some
    /// inputs "may legitimately parse", so despite its name it asserted nothing about rejection —
    /// a regression returning a Version for "abc" or "1,2,3" passed all 18 cases. The comment was
    /// also wrong: every input here returns null, "1.2.3.4.5.6" included (its four-plus components
    /// survive the suffix cut, and Version.TryParse rejects six).</para>
    /// <para>It matters because ParseVersion decides whether the user is told an update exists. A
    /// garbage tag that parsed to some arbitrary version could offer a downgrade, or hide a real
    /// update behind a bogus higher number.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Garbage))]
    public void ParseVersion_RejectsAllGarbage(string tag)
    {
        Assert.Null(UpdateService.ParseVersion(tag));
    }

    public static IEnumerable<object[]> NewerPairs()
    {
        for (var a = 0; a <= 5; a++)
            for (var b = 0; b <= 5; b++)
                if (a != b)
                    yield return new object[] { $"0.{a}.0", $"0.{b}.0", a > b };
    }

    [Theory]
    [MemberData(nameof(NewerPairs))]
    public void IsNewer_MinorComparisons(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(Version.Parse(latest), Version.Parse(current)));
    }

    public static IEnumerable<object[]> PatchPairs()
    {
        for (var a = 0; a <= 5; a++)
            for (var b = 0; b <= 5; b++)
                if (a != b)
                    yield return new object[] { $"0.5.{a}", $"0.5.{b}", a > b };
    }

    [Theory]
    [MemberData(nameof(PatchPairs))]
    public void IsNewer_PatchComparisons(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(Version.Parse(latest), Version.Parse(current)));
    }
}
