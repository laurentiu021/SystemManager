// SysManager · WelcomeViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Input;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The one-time Welcome screen (#1635): up only where it has not been seen, and every way out records it, so it does
/// not come back.
/// </summary>
public class WelcomeViewModelTests
{
    private readonly IFirstRunService _firstRun = Substitute.For<IFirstRunService>();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();

    private WelcomeViewModel NewWelcome(bool seen = false)
    {
        _firstRun.HasSeenWelcome().Returns(seen);
        _firstRun.MarkWelcomeSeen().Returns(true);
        return new WelcomeViewModel(_firstRun, _navigation);
    }

    [Fact]
    public void OnAPcWhereItHasNotBeenSeen_TheWelcomeIsUp()
        => Assert.True(NewWelcome(seen: false).IsVisible);

    [Fact]
    public void OnceSeen_TheWelcomeStaysDown_AndNothingIsWritten()
    {
        var welcome = NewWelcome(seen: true);

        welcome.RememberSeen();

        Assert.False(welcome.IsVisible);
        _firstRun.DidNotReceive().MarkWelcomeSeen();
    }

    [Fact]
    public void CheckMyPcsHealth_ClosesItForGood_AndOpensSystemHealth()
    {
        var welcome = NewWelcome();

        welcome.CheckHealthCommand.Execute(null);

        Assert.False(welcome.IsVisible);
        _firstRun.Received(1).MarkWelcomeSeen();
        _navigation.Received(1).GoTo("nav-system-health", Arg.Any<string?>());
    }

    [Fact]
    public void CleanUpJunkFiles_ClosesItForGood_AndOpensQuickCleanup()
    {
        var welcome = NewWelcome();

        welcome.CleanUpCommand.Execute(null);

        Assert.False(welcome.IsVisible);
        _firstRun.Received(1).MarkWelcomeSeen();
        _navigation.Received(1).GoTo("nav-cleanup", Arg.Any<string?>());
    }

    [Fact]
    public void JustLookAround_ClosesItForGood_AndOpensNothing()
    {
        var welcome = NewWelcome();

        welcome.LookAroundCommand.Execute(null);

        Assert.False(welcome.IsVisible);
        _firstRun.Received(1).MarkWelcomeSeen();
        _navigation.DidNotReceiveWithAnyArgs().GoTo(default!, default);
    }

    [Fact]
    public void Escape_IsJustLookAround()
    {
        var welcome = NewWelcome();

        Assert.Same(welcome.LookAroundCommand, welcome.KeyCommand(Key.Escape));
    }

    /// <summary>
    /// The shell gives no key but Escape to anything while the screen is up, so these do nothing: F1 would open About,
    /// F5 refresh the Dashboard, and Enter, Space and Tab belong to the focused button.
    /// </summary>
    [Theory]
    [InlineData(Key.F1)]
    [InlineData(Key.F5)]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Delete)]
    public void NoOtherKey_RunsAnything(Key key)
        => Assert.Null(NewWelcome().KeyCommand(key));

    /// <summary>
    /// Closing the window counts as seen, and leaves the screen up: the close can still be cancelled at the question
    /// that follows, and then the window should come back as it was.
    /// </summary>
    [Fact]
    public void ClosingTheWindow_RecordsItAsSeen_AndLeavesItUp()
    {
        var welcome = NewWelcome();

        welcome.RememberSeen();

        Assert.True(welcome.IsVisible);
        _firstRun.Received(1).MarkWelcomeSeen();
    }

    [Fact]
    public void TheRecordIsWrittenOnce_HoweverManyWaysOutAreTaken()
    {
        var welcome = NewWelcome();

        welcome.RememberSeen();
        welcome.RememberSeen();
        welcome.LookAroundCommand.Execute(null);
        welcome.RememberSeen();

        _firstRun.Received(1).MarkWelcomeSeen();
    }

    [Fact]
    public void ARecordThatCouldNotBeWritten_IsTriedAgainAtTheNextWayOut()
    {
        var welcome = NewWelcome();
        _firstRun.MarkWelcomeSeen().Returns(false, true);

        welcome.RememberSeen();
        welcome.LookAroundCommand.Execute(null);

        _firstRun.Received(2).MarkWelcomeSeen();
        Assert.False(welcome.IsVisible);
    }

    [Fact]
    public void ARecordThatCouldNotBeWritten_StillClosesTheScreen()
    {
        var welcome = NewWelcome();
        _firstRun.MarkWelcomeSeen().Returns(false);

        welcome.CheckHealthCommand.Execute(null);

        Assert.False(welcome.IsVisible);
        _navigation.Received(1).GoTo("nav-system-health", Arg.Any<string?>());
    }
}
