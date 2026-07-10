// SysManager · SpeedTestServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SpeedTestService"/>'s Zip Slip containment check, which
/// guards the Ookla CLI extraction against crafted archive entries escaping the
/// target tools directory.
/// </summary>
public class SpeedTestServiceTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "smtest_tools");

    [Fact]
    public void IsInsideDirectory_NormalEntry_IsAccepted()
    {
        var dest = Path.GetFullPath(Path.Join(Root, "speedtest.exe"));
        Assert.True(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_NestedEntry_IsAccepted()
    {
        var dest = Path.GetFullPath(Path.Join(Root, "sub", "license.txt"));
        Assert.True(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_TraversalEntry_IsRejected()
    {
        // A "../" entry that resolves to the parent directory must be rejected.
        var dest = Path.GetFullPath(Path.Join(Root, "..", "evil.exe"));
        Assert.False(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public void IsInsideDirectory_SiblingWithSharedPrefix_IsRejected()
    {
        // Regression: a sibling directory whose name merely starts with the target's
        // name (e.g. "smtest_tools-evil") must NOT pass the containment check. A naive
        // StartsWith(fullToolsDir) without a trailing separator would wrongly accept it.
        var dest = Path.GetFullPath(Path.Combine(Root + "-evil", "payload.exe"));
        Assert.False(SpeedTestService.IsInsideDirectory(Root, dest));
    }

    [Fact]
    public async Task RunOoklaAsync_UserCancelDuringPrepare_SurfacesAsCancellation()
    {
        // Regression: the prepare phase (EnsureOoklaAsync) was wrapped in a blanket
        // catch (Exception) that re-threw OperationCanceledException as
        // InvalidOperationException("Could not prepare Ookla CLI: A task was canceled."),
        // bypassing the ViewModel's dedicated "Cancelled" handler and misreporting a
        // clean user cancel as an error. A pre-cancelled token makes the first
        // Task.Run(..., ct) inside EnsureOoklaAsync throw before any network or
        // filesystem work, so this test is deterministic and offline.
        var svc = new SpeedTestService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunOoklaAsync(progress: null, cts.Token));
    }
}
