// SysManager · SfcOutputTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// <see cref="SfcOutput"/> reads SFC's progress line and the sentences it ends a scan with by the first lines of SFC's
/// own messages (#2835). This Windows' message table is read in the integration suite's <c>SfcMessageTableTests</c>.
/// </summary>
public class SfcOutputTests
{
    [Fact]
    public void EveryOutcome_HasOneEnglishSentence_WorstFirst()
    {
        Assert.Equal(Enum.GetValues<SfcOutcome>(), SfcOutput.Messages.Select(m => m.Outcome));
        // No two share a first line, so none can be read as another.
        Assert.Equal(SfcOutput.Messages.Count, SfcOutput.English.Count);
    }

    [Fact]
    public void Encoding_IsUtf16LittleEndian() => Assert.Equal(1200, SfcOutput.Encoding.CodePage);

    [Theory]
    [InlineData("Verification 0% complete.", 0)]
    [InlineData("Verification 57% complete.", 57)]
    [InlineData("Verification 100% complete.", 100)]
    [InlineData("Verification 57 % complete.", 57)]
    // A translation can put the sign before the number: the line is read by its number either way.
    [InlineData("Verification %42 complete.", 42)]
    public void ProgressIn_ReadsThePercentage(string line, int percent) =>
        Assert.Equal(percent, SfcOutput.ProgressIn(line));

    [Theory]
    [InlineData("")]
    [InlineData("Beginning verification phase of system scan.")]
    // The one other line of SFC's with a percent sign has no number in it.
    [InlineData(@"%windir%\Logs\CBS\CBS.log. For example C:\Windows\Logs\CBS\CBS.log. For offline")]
    [InlineData("Microsoft (R) Windows (R) Resource Checker Version 6.0")]
    [InlineData("Verification 101% complete.")]
    public void ProgressIn_IsNull_ForAnyOtherLine(string line) => Assert.Null(SfcOutput.ProgressIn(line));

    [Fact]
    public void OutcomesIn_FindsEachSentence_WithTheSpacesSfcPrintsAroundIt()
    {
        // SFC breaks a long message in the middle of a sentence and keeps the space before the break.
        foreach (var (outcome, _, english) in SfcOutput.Messages)
            Assert.Equal(outcome, Assert.Single(SfcOutput.OutcomesIn([$"  {english} "], SfcOutput.English)));
    }

    [Fact]
    public void OutcomesIn_ReadsOnlyWholeFirstLines()
    {
        // The healthy sentence is a prefix of the first line that says the store is too damaged to be sure, and the
        // English phrase the tab matched before read that one as healthy.
        var seen = SfcOutput.OutcomesIn(
            ["Windows Resource Protection did not find any integrity violations but cannot"], SfcOutput.English);
        Assert.Equal(SfcOutcome.CannotGuarantee, Assert.Single(seen));
        Assert.Empty(SfcOutput.OutcomesIn(["did not find any integrity violations"], SfcOutput.English));
        // Nor a line that only has a sentence in it: a match is the whole line, so no message is read inside another
        // that begins with its words.
        Assert.Empty(SfcOutput.OutcomesIn(
            ["Note: Windows Resource Protection did not find any integrity violations."], SfcOutput.English));
    }

    [Fact]
    public void OutcomesIn_ReadsTheTableItIsGiven()
    {
        var translated = new Dictionary<string, SfcOutcome>(StringComparer.Ordinal)
        {
            ["Alpha bravo charlie."] = SfcOutcome.Repaired,
        };
        Assert.Equal(SfcOutcome.Repaired, Assert.Single(SfcOutput.OutcomesIn(["Alpha bravo charlie."], translated)));
        Assert.Empty(SfcOutput.OutcomesIn([SfcOutput.English.Keys.First()], translated));
    }
}
