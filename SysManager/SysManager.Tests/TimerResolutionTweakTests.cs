// SysManager · TimerResolutionTweakTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the Gaming Profile <c>TimerResolutionTweak</c> (#2444). The timer service is substituted, so no real
/// <c>NtSetTimerResolution</c> is issued. Windows keeps one timer request per process, so what the step releases on
/// revert decides whether Stop takes away a request the Timer Resolution tab made.
/// </summary>
public sealed class TimerResolutionTweakTests
{
    private static TimerResolutionStatus Status(bool enabledByApp)
        => new(FinestHundredNs: 5000, CoarsestHundredNs: 156250, CurrentHundredNs: enabledByApp ? 5000u : 156250u, enabledByApp);

    /// <summary>A timer with no request of SysManager's before the session; Enable answers <paramref name="accepted"/>.</summary>
    private static ITimerResolutionService TimerWithoutARequest(bool accepted)
    {
        var timer = Substitute.For<ITimerResolutionService>();
        timer.Query().Returns(Status(enabledByApp: false));
        timer.Enable().Returns(Status(enabledByApp: accepted));
        return timer;
    }

    [Fact]
    public async Task Apply_WhenWindowsAcceptsTheRequest_ReportsApplied_AndTheRevertReleasesIt()
    {
        var timer = TimerWithoutARequest(accepted: true);
        var tweak = new TimerResolutionTweak(timer);

        Assert.Equal(GamingTweakResult.Applied, await tweak.ApplyAsync(default));
        await tweak.RevertAsync(default);

        timer.Received(1).Disable();
    }

    [Fact]
    public async Task Apply_WhenWindowsRefusesTheRequest_ReportsFailed_NotApplied()
    {
        // The step returned Applied whatever Enable() came back with, so a refused request was counted in
        // "Game mode on — N optimization(s) applied".
        var timer = TimerWithoutARequest(accepted: false);

        Assert.Equal(GamingTweakResult.Failed, await new TimerResolutionTweak(timer).ApplyAsync(default));
    }

    [Fact]
    public async Task WhenTheTimerTabAlreadyHoldsARequest_TheSessionChangesNothing_AndStopLeavesItInPlace()
    {
        // Driven through the engine, as Start and Stop drive it: a NoChange step is not tracked for revert.
        var timer = Substitute.For<ITimerResolutionService>();
        timer.Query().Returns(Status(enabledByApp: true));
        var applied = new List<IGamingTweak>();

        var outcomes = await GamingProfileService.RunApplyAsync(
            [new TimerResolutionTweak(timer)], isElevated: false, applied, default);
        await GamingProfileService.RunRevertAsync(applied, default);

        Assert.Equal(GamingStepStatus.SkippedNoChange, Assert.Single(outcomes).Status);
        timer.DidNotReceive().Enable();
        timer.DidNotReceive().Disable();
    }

    [Fact]
    public async Task Revert_OfAStepThatMadeNoRequest_ReleasesNothing()
    {
        // Crash recovery rebuilds the step from the saved profile and reverts it without applying it. The crashed
        // run's request ended with its process, so a release here could only drop one made in this run.
        var timer = Substitute.For<ITimerResolutionService>();

        await new TimerResolutionTweak(timer).RevertAsync(default);

        timer.DidNotReceive().Disable();
    }

    [Fact]
    public async Task Revert_IsIdempotent()
    {
        var timer = TimerWithoutARequest(accepted: true);
        var tweak = new TimerResolutionTweak(timer);
        await tweak.ApplyAsync(default);

        await tweak.RevertAsync(default);
        await tweak.RevertAsync(default);

        timer.Received(1).Disable();
    }
}
