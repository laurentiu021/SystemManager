// SysManager · RecycleBinHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// What the shared "send to the Recycle Bin" asks the shell for (#1527). Shortcut Cleaner and the Uninstaller's
/// leftovers both tell the user that what they remove can be put back, and these flags are what keeps that true.
/// </summary>
/// <remarks>
/// Asserted on the constant and on the source, never by calling the shell: a call would put a real item in the
/// Recycle Bin of the PC running the suite. The constant alone would not be enough, because a constant nobody passes
/// protects nothing, so the second test pins that the operation is built from it.
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
}
