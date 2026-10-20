// SysManager · RecycleBinShellTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The shell answers the Recycle Bin query the way SysManager asks it, on a real Windows.
/// </summary>
/// <remarks>
/// Read-only: it asks what the bin holds and never empties it. Every unit test hands the code a substitute bin, so a
/// layout of <c>SHQUERYRBINFO</c> the shell refused, or one it filled in at the wrong offsets, would show nowhere but
/// here: Quick Cleanup would read "Unable to scan" and Deep Cleanup would count the bin as skipped, on every PC.
/// </remarks>
public sealed class RecycleBinShellTests
{
    [Fact]
    public void TheShellAnswers_WithCountsThatCanBeTrue()
    {
        var holds = new ShellRecycleBin().Query();

        Assert.NotNull(holds);
        var bin = holds.Value;
        Assert.True(bin.Bytes >= 0 && bin.Items >= 0, bin.ToString());
        // Bytes with no items to hold them is a struct read at the wrong offsets, not a bin.
        Assert.False(bin.Items == 0 && bin.Bytes > 0, bin.ToString());
    }
}
