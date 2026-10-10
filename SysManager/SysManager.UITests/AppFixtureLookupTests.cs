// SysManager · AppFixtureLookupTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;

namespace SysManager.UITests;

/// <summary>
/// A UI Automation lookup that times out is asked again within its budget, and nothing else is.
/// </summary>
/// <remarks>
/// <c>AppFixture.GoToTab</c> looked its nav item up with one bare call, which UI Automation failed with
/// "Operation timed out" (0x80131505) while the app was busy, and the test failed at the lookup (#2655). These run
/// the retry on a clock and a pause of their own, so they wait for nothing and launch no app: they are not in the
/// <c>App</c> collection.
/// </remarks>
public class AppFixtureLookupTests
{
    [Fact]
    public void ALookupThatTimesOut_IsAskedAgain_AndItsAnswerReturned()
    {
        var asked = 0;
        var clock = TimeSpan.Zero;

        var answer = AppFixture.AskAgainWhileTimedOut(
            () => ++asked < 3 ? throw TimedOut() : "found", AppFixture.LookupBudget, () => clock, wait => clock += wait);

        Assert.Equal("found", answer);
        Assert.Equal(3, asked);
        Assert.Equal(2 * AppFixture.AskAgainAfter, clock);
    }

    [Fact]
    public void ALookupThatKeepsTimingOut_FailsOnceItsBudgetIsSpent()
    {
        var asked = 0;
        var clock = TimeSpan.Zero;

        var ex = Assert.Throws<COMException>(() => AppFixture.AskAgainWhileTimedOut<string>(
            () => { asked++; throw TimedOut(); }, AppFixture.LookupBudget, () => clock, wait => clock += wait));

        Assert.Equal(AppFixture.TimedOut, ex.HResult);
        Assert.Equal((int)(AppFixture.LookupBudget / AppFixture.AskAgainAfter) + 1, asked);
    }

    [Theory]
    [InlineData("an element that has gone")]
    [InlineData("a failure of another kind")]
    public void ALookupThatFailsAnotherWay_FailsAtOnce(string failure)
    {
        // UIA_E_ELEMENTNOTAVAILABLE is the COM failure most like a timeout, and it is NameOf's to handle, not this.
        Exception thrown = failure == "an element that has gone"
            ? new COMException("Element not available.", unchecked((int)0x80040201))
            : new InvalidOperationException("Not a lookup failure.");
        var asked = 0;
        var clock = TimeSpan.Zero;

        // The clock moves on each pause, so a retry that took every failure would still end, and be counted.
        var ex = Record.Exception(() => AppFixture.AskAgainWhileTimedOut<string>(
            () => { asked++; throw thrown; }, AppFixture.LookupBudget, () => clock, wait => clock += wait));

        Assert.Same(thrown, ex);
        Assert.Equal(1, asked);
    }

    private static COMException TimedOut() => new("Operation timed out.", AppFixture.TimedOut);
}
