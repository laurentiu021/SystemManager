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

    public static IEnumerable<object[]> TwoComponentTags()
    {
        // The bare shape, both casings, surrounding whitespace, and every suffix the parser strips —
        // the strip happens BEFORE the parse, so a suffixed tag reduces to exactly the same two
        // components and must be normalised the same way.
        var tags = new[] { "v1.2", "1.2", "V1.2", " v1.2 ", "v1.2-beta", "1.2-rc.2", "v1.2+build" };
        foreach (var t in tags) yield return new object[] { t };
    }

    /// <summary>
    /// A two-component tag must come back with a <c>Build</c> of 0, not the -1
    /// <c>Version.TryParse</c> leaves behind.
    /// </summary>
    /// <remarks>
    /// <c>Version.TryParse("1.2")</c> SUCCEEDS — it is not garbage, it is under-specified — and the
    /// two-component shape is the only under-specified one that gets in, because a single component
    /// ("v1", already in <see cref="Garbage"/>) is rejected outright. Normalising at the parse
    /// boundary is what keeps every consumer safe; this test is the one that notices if the
    /// normalisation is ever "simplified" away.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TwoComponentTags))]
    public void ParseVersion_NormalisesATwoComponentTag(string tag)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.Equal(1, v!.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(0, v.Build);
    }

    /// <summary>
    /// The value a release carries has to survive the label About builds from it.
    /// </summary>
    /// <remarks>
    /// This formats rather than inspecting <c>Build</c>, because the formatting is the actual defect:
    /// <c>Version.ToString(3)</c> throws <see cref="ArgumentException"/> below three components, and
    /// both places that format a PARSED release version — the latest-version label and every row of
    /// the release-history list — sit in commands that catch only <c>HttpRequestException</c> and
    /// <c>TaskCanceledException</c>. The exception would escape to the dispatcher.
    /// <para>The four-component row is here to record that <c>ToString(3)</c> truncates rather than
    /// throwing above three, so only the short shape ever needed fixing.</para>
    /// </remarks>
    [Theory]
    [InlineData("v1.2", "v1.2.0")]
    [InlineData("1.2", "v1.2.0")]
    [InlineData("v1.2-beta", "v1.2.0")]
    [InlineData("v1.2.3", "v1.2.3")]
    [InlineData("v1.2.3.4", "v1.2.3")]
    [InlineData("v0.5.0-rc.2", "v0.5.0")]
    public void ParseVersion_ResultSurvivesTheLabelAboutBuildsFromIt(string tag, string expectedLabel)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.Equal(expectedLabel, $"v{v!.ToString(3)}");
    }

    /// <summary>
    /// Normalising the component count must not COST one: a four-component tag keeps its revision.
    /// </summary>
    /// <remarks>
    /// The obvious one-line normalisation — <c>new Version(v.Major, v.Minor, Math.Max(v.Build, 0))</c>
    /// — silently drops <c>Revision</c>, which would make "v1.2.3.4" compare EQUAL to "v1.2.3" and
    /// hide a real update rather than merely mis-format one. That is why the fix normalises only when
    /// <c>Build</c> is negative instead of rebuilding unconditionally.
    /// </remarks>
    [Theory]
    [InlineData("v1.2.3.4")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3.4-beta")]
    public void ParseVersion_KeepsAFourthComponent(string tag)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.Equal(4, v!.Revision);
        Assert.True(UpdateService.IsNewer(v, new Version(1, 2, 3)));
    }

    /// <summary>
    /// "1.2" means "1.2.0", and after normalisation it compares as such.
    /// </summary>
    /// <remarks>
    /// Formatting is not the only thing that broke: an un-normalised two-component version sorts
    /// BELOW the same patch-zero release (<c>Build</c> of -1 against 0), so a "1.2" tag would have
    /// been judged OLDER than the 1.2.0 the user is already running — and <see cref="UpdateService.IsNewer"/>
    /// is what decides whether an update is offered at all.
    /// </remarks>
    [Fact]
    public void ParseVersion_TwoComponentTagComparesAsThePatchZeroItMeans()
    {
        var parsed = UpdateService.ParseVersion("v1.2");
        Assert.Equal(new Version(1, 2, 0), parsed);
        Assert.False(UpdateService.IsNewer(parsed!, new Version(1, 2, 0)));
        Assert.False(UpdateService.IsNewer(new Version(1, 2, 0), parsed!));
        Assert.True(UpdateService.IsNewer(parsed!, new Version(1, 1, 9)));
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
