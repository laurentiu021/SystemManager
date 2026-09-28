// SysManager · QuitGuardTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// <see cref="QuitGuard"/>: what SysManager says before it closes while something is still running (#2499).
/// </summary>
/// <remarks>
/// The running work is real locks on <see cref="OperationLockService.Instance"/>, the same registry every
/// system-changing operation writes to, released in each test's own <c>using</c>.
/// </remarks>
// Serialized: these hold OperationLockService locks and swap DialogService.Instance, both process-wide.
[Collection("ProcessWideStatics")]
public class QuitGuardTests
{
    [Fact]
    public void NothingRunning_AddsNothingAndAsksNothing()
    {
        Assert.False(OperationLockService.Instance.HasActiveOperations,
            "an earlier test left an operation lock held, so this cannot see the idle case");
        using var dialog = new DialogAnswer(confirm: false);

        Assert.Equal("", QuitGuard.ActiveWorkWarning());
        Assert.True(QuitGuard.ConfirmStoppingActiveWork("Exit SysManager", "Exit anyway?"));
        Assert.Equal(0, dialog.Calls);
    }

    [Fact]
    public void SomethingRunning_NamesIt_AndDecliningKeepsSysManagerOpen()
    {
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        Assert.NotNull(held);
        using var dialog = new DialogAnswer(confirm: false);

        var goAhead = QuitGuard.ConfirmStoppingActiveWork("Exit SysManager", "Exit anyway?");

        Assert.False(goAhead);
        var shown = Assert.Single(dialog.Messages);
        Assert.StartsWith("Exit SysManager\n", shown);
        Assert.Contains("still working on: SFC scan.", shown);
        Assert.Contains("stops part-way", shown);
        Assert.EndsWith("Exit anyway?", shown);
    }

    [Fact]
    public void SomethingRunning_Confirmed_LetsTheExitGoAhead()
    {
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup");
        Assert.NotNull(held);
        using var dialog = new DialogAnswer(confirm: true);

        Assert.True(QuitGuard.ConfirmStoppingActiveWork("Run as administrator", "Restart as administrator anyway?"));
        Assert.Equal(1, dialog.Calls);
    }

    [Fact]
    public void TwoOperationsRunning_BothAreNamed()
    {
        using var repair = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "DISM RestoreHealth");
        using var clean = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup");
        Assert.NotNull(repair);
        Assert.NotNull(clean);

        var warning = QuitGuard.ActiveWorkWarning();

        Assert.StartsWith("\n\n", warning);   // appended to a prompt that already has its own first paragraph
        Assert.Contains("DISM RestoreHealth", warning);
        Assert.Contains("Deep Cleanup", warning);
    }

    [Fact]
    public void AfterTheWorkFinishes_NothingIsSaid()
    {
        // The warning reads the registry at the moment of asking, so a finished operation does not linger in it.
        // Released in a finally, not after the first assertion: a lock left held by a failed assertion would make
        // every later test in this collection see an operation that is not running.
        var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        Assert.NotNull(held);
        try
        {
            Assert.NotEqual("", QuitGuard.ActiveWorkWarning());
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal("", QuitGuard.ActiveWorkWarning());
    }
}
