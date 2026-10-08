// SysManager · DashboardTabUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.UITests;

[Collection("App")]
public class DashboardTabUiTests
{
    private readonly AppFixture _fx;
    public DashboardTabUiTests(AppFixture fx) => _fx = fx;

    private void GoTo() => _fx.GoToTab("nav-dashboard");

    [Fact]
    public void ScanSystemButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButtonById("btn-dashboard-scan-system"));
    }

    /// <summary>
    /// "Why is it slow?" looks, and ends on how many of the five it could look at (#1529). Whatever this machine is doing,
    /// the card says when it checked and how much it looked at, so that line is what proves the check ran end to end in
    /// the real app: the button, the lock, the five probes on a real PC, and the card drawing the result.
    /// </summary>
    /// <remarks>
    /// The wait reads names through <see cref="AppFixture.NameOf"/>, because the list of five is redrawn row by row while
    /// it waits. A check still running when the wait gives up is cancelled, so the tests after this one do not start with
    /// the Disk lock held.
    /// </remarks>
    [Fact]
    public void WhyIsItSlow_Looks_AndSaysWhatItLookedAt()
    {
        GoTo();
        var check = _fx.FindButtonById("btn-dashboard-why-slow");
        Assert.NotNull(check);

        check!.Invoke();
        var shown = _fx.HasTextInCurrentTab("things looked at", timeoutSeconds: 60);
        var stillLooking = !shown && _fx.HasButtonWithName("Cancel the slowness check");
        if (stillLooking) _fx.FindButtonByAccessibleName("Cancel the slowness check")!.Invoke();

        Assert.True(shown, stillLooking
            ? "the slowness check was still looking after a minute, and was cancelled"
            : "the slowness check did not show what it looked at, and was not running either");
    }

    [Fact]
    public void SectionLabels_Present()
    {
        GoTo();
        // The live-vitals tiles: CPU / MEMORY / GPU, plus the Storage section.
        // (WaitForText is case-insensitive, so "Memory" matches the "MEMORY" tile.)
        Assert.NotNull(_fx.WaitForText("CPU"));
        Assert.NotNull(_fx.WaitForText("Memory"));
        Assert.NotNull(_fx.WaitForText("Storage"));
    }
}
