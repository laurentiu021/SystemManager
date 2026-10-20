// SysManager · RecycleBinHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// What the shared "send to the Recycle Bin" asks the shell for (#1527), and what emptying the bin decides from what the
/// shell reports (#2800). Shortcut Cleaner and the Uninstaller's leftovers both tell the user that what they remove can
/// be put back, and the flags are what keeps that true.
/// </summary>
/// <remarks>
/// Never by calling the shell: a call would put a real item in the Recycle Bin of the PC running the suite, or empty
/// it. The flags are asserted on the constant and on the source, because a constant nobody passes protects nothing, so
/// the second test pins that the operation is built from it. The emptying is handed stand-ins for its two shell calls.
/// </remarks>
public sealed class RecycleBinHelperTests
{
    private const ushort AllowUndo = 0x0040;
    private const ushort NoConfirmation = 0x0010;
    private const ushort WantNukeWarning = 0x4000;

    [Fact]
    public void TheShellIsAskedToRecycle_AndToWarnBeforeDeletingForGood()
    {
        // Without the nuke warning, an item too big for the bin, or on a drive whose bin is off, is deleted for good
        // without a word, because "no confirmation" also answers that question.
        Assert.Equal(AllowUndo | NoConfirmation | WantNukeWarning, RecycleBinHelper.RecycleFlags);
    }

    [Fact]
    public void TheRecycleOperation_IsBuiltFromThoseFlags()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppProject(), "Helpers", "RecycleBinHelper.cs"));
        var code = string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.Contains("public static bool SendToRecycleBin(string path)", code, StringComparison.Ordinal);

        // Every flags assignment in the file, so a second operation with its own literal flags cannot slip in beside it.
        var assignments = Regex.Matches(code, @"\bfFlags\s*=\s*([^,;\r\n]+)").Select(m => m.Groups[1].Value.Trim()).ToList();
        Assert.Equal(["RecycleFlags"], assignments);
    }

    // ── Emptying: what the shell is asked, and what the result says (#2800) ────

    private static readonly RecycleBinContents Holding = new(4096, 3);
    private const int Ok = 0;
    private const int Unexpected = unchecked((int)0x8000FFFF);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int NoMoreFiles = unchecked((int)0x80070012);

    /// <summary>Answers each query with the next of <paramref name="answers"/>, the last one from then on.</summary>
    private sealed class FakeShell(params RecycleBinContents?[] answers)
    {
        private int _next;

        public int Empties { get; private set; }

        public RecycleBinContents? Query() => answers[Math.Min(_next++, answers.Length - 1)];

        public Func<int> EmptyReturning(int hr) => () =>
        {
            Empties++;
            return hr;
        };
    }

    [Fact]
    public void AnEmptyBin_IsNotEmptiedAgain_AndReadsAsAlreadyEmpty()
    {
        var shell = new FakeShell(new RecycleBinContents(0, 0));

        var result = RecycleBinHelper.Empty(shell.Query, shell.EmptyReturning(Unexpected));

        Assert.Equal(0, shell.Empties);
        Assert.True(result.Emptied);
        Assert.True(result.WasAlreadyEmpty);
    }

    [Fact]
    public void ABinWithItems_IsEmptied_AndTheResultSaysWhatItHeld()
    {
        var shell = new FakeShell(Holding);

        var result = RecycleBinHelper.Empty(shell.Query, shell.EmptyReturning(Ok));

        Assert.Equal(1, shell.Empties);
        Assert.Equal(new RecycleBinEmptyResult(Emptied: true, Holding), result);
        Assert.False(result.WasAlreadyEmpty);
    }

    [Fact]
    public void AFailureCode_WithTheBinEmptyAfterwards_CountsAsEmptied()
    {
        // The bin, not the code, says whether the user has what they asked for.
        var shell = new FakeShell(Holding, new RecycleBinContents(0, 0));

        Assert.Equal(new RecycleBinEmptyResult(Emptied: true, Holding),
            RecycleBinHelper.Empty(shell.Query, shell.EmptyReturning(Unexpected)));
    }

    [Fact]
    public void ARefusalThatLeavesItemsBehind_IsAFailure()
    {
        var shell = new FakeShell(Holding, Holding);

        Assert.Equal(new RecycleBinEmptyResult(Emptied: false, Holding),
            RecycleBinHelper.Empty(shell.Query, shell.EmptyReturning(AccessDenied)));
    }

    [Fact]
    public void ABinTheShellCannotMeasure_IsStillEmptied_AndARefusalStillFails()
    {
        var shell = new FakeShell(null, null);

        Assert.Equal(new RecycleBinEmptyResult(Emptied: true, Before: null),
            RecycleBinHelper.Empty(shell.Query, shell.EmptyReturning(Ok)));
        Assert.True(RecycleBinHelper.Empty(shell.Query, shell.EmptyReturning(NoMoreFiles)).Emptied);
        Assert.False(RecycleBinHelper.Empty(shell.Query, shell.EmptyReturning(AccessDenied)).Emptied);
        Assert.Equal(3, shell.Empties);
    }

    [Theory]
    [InlineData(1L, 512L, "1 item, 512 B")]
    [InlineData(12L, 3L << 30, "12 items, 3.0 GB")]
    [InlineData(1234L, 0L, "1,234 items, 0 B")]
    public void WhatABinHeld_ReadsAsItemsAndSize(long items, long bytes, string expected)
        => Assert.Equal(expected, new RecycleBinContents(bytes, items).Display);
}
