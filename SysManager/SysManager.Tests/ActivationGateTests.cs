// SysManager · ActivationGateTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>The gate a tab's poll loop waits at while the tab is hidden (#2853).</summary>
public class ActivationGateTests
{
    [Fact]
    public void AnOpenGate_LetsAWaitThroughAtOnce()
    {
        var gate = new ActivationGate(open: true);

        Assert.True(gate.IsOpen);
        Assert.True(gate.WaitAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public void AClosedGate_HoldsAWait_UntilItOpens()
    {
        var gate = new ActivationGate(open: false);

        var wait = gate.WaitAsync(CancellationToken.None);
        Assert.False(gate.IsOpen);
        Assert.False(wait.IsCompleted);

        gate.Open();
        Assert.True(wait.IsCompletedSuccessfully);
        Assert.True(gate.IsOpen);
    }

    /// <summary>Closed again after it opened, it holds the next wait: a one-shot gate would leave a tab dead.</summary>
    [Fact]
    public void AGateClosedAfterItOpened_HoldsTheNextWait()
    {
        var gate = new ActivationGate(open: true);

        gate.Close();

        Assert.False(gate.IsOpen);
        Assert.False(gate.WaitAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public void OpeningOrClosingTwice_IsTheSameAsOnce()
    {
        var gate = new ActivationGate(open: false);
        gate.Open();
        gate.Open();
        Assert.True(gate.IsOpen);

        gate.Close();
        var held = gate.WaitAsync(CancellationToken.None);
        gate.Close();
        Assert.False(gate.IsOpen);

        gate.Open();
        Assert.True(held.IsCompletedSuccessfully);   // the second Close did not leave the first wait behind
    }

    /// <summary>Cancelled at once, so a tab's loop ends with the tab rather than when it is next shown.</summary>
    [Fact]
    public void AWaitAtAClosedGate_EndsWhenItsTokenIsCancelled()
    {
        var gate = new ActivationGate(open: false);
        using var cts = new CancellationTokenSource();

        var wait = gate.WaitAsync(cts.Token);
        Assert.False(wait.IsCompleted);
        cts.Cancel();

        Assert.True(wait.IsCanceled);
    }
}
