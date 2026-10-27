// SysManager · UiFaultDecisionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager;

namespace SysManager.Tests;

/// <summary>
/// Tests for <c>App.DecideUiFault</c>: what a dispatcher exception means, and in particular the ORDER of
/// its arms.
/// </summary>
/// <remarks>
/// <para>The fault this pins (#2863): the dispatcher handler set <c>e.Handled = true</c> for everything and
/// never asked whether a window existed. A throw while <c>MainWindow</c> was being built — its
/// <c>MainWindowViewModel</c> is constructed by the XAML, which resolves several view models eagerly — left
/// a SysManager with no window (the tray is only initialised from <c>OnSourceInitialized</c>, which never
/// ran), no crash marker (only the domain handler wrote one, and handling the exception means it never
/// sees it), and the single-instance mutex still held. Every later launch handed over to that process and
/// exited, so the app could not be started again without Task Manager.</para>
/// <para>The decision is a pure function precisely so this is assertable without raising a real dispatcher
/// exception on a window that does not exist.</para>
/// </remarks>
public class UiFaultDecisionTests
{
    /// <summary>A fault before the window loaded ends the process rather than being handled.</summary>
    [Fact]
    public void BeforeTheWindowLoads_AFaultFailsStartup() =>
        Assert.Equal(App.UiFaultAction.FailStartup,
            App.DecideUiFault(new InvalidOperationException("a view model threw"), mainWindowLoaded: false));

    /// <summary>The same fault after the window loaded is survivable: there is something to go back to.</summary>
    [Fact]
    public void AfterTheWindowLoads_TheSameFaultKeepsTheAppRunning() =>
        Assert.Equal(App.UiFaultAction.ShowAndKeepRunning,
            App.DecideUiFault(new InvalidOperationException("a view model threw"), mainWindowLoaded: true));

    /// <summary>
    /// Teardown noise is swallowed whether or not the window loaded — the arms are ordered so the
    /// swallows come FIRST.
    /// </summary>
    /// <remarks>
    /// This is the assertion that stops the fix from causing a worse bug than the one it fixes. Services
    /// and cancellation-token sources are disposed while async operations are still in flight, which is
    /// expected; if the startup check ran first, any one of those during start-up would end the process
    /// and report a crash that never happened.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TeardownNoiseIsSwallowedWhicheverSideOfLoadedItArrivesOn(bool loaded)
    {
        Assert.Equal(App.UiFaultAction.Swallow,
            App.DecideUiFault(new ObjectDisposedException("Service"), loaded));
        Assert.Equal(App.UiFaultAction.Swallow,
            App.DecideUiFault(new OperationCanceledException(), loaded));
        // The disposed-object case that arrives as an InvalidOperationException, matched on its message.
        Assert.Equal(App.UiFaultAction.Swallow,
            App.DecideUiFault(new InvalidOperationException("The CancellationTokenSource has been disposed."), loaded));
        // And the one that arrives wrapped, which is how an await surfaces it.
        Assert.Equal(App.UiFaultAction.Swallow,
            App.DecideUiFault(new AggregateException("wrapped", new ObjectDisposedException("Service")), loaded));
    }

    /// <summary>
    /// An ordinary <see cref="InvalidOperationException"/> is NOT swallowed just because the word
    /// "disposed" could have appeared.
    /// </summary>
    /// <remarks>
    /// The swallow is matched on the message, which is loose by nature, so the negative case is worth
    /// pinning: a real startup fault whose message happens not to mention disposal must still fail.
    /// </remarks>
    [Fact]
    public void AnUnrelatedInvalidOperationStillFailsStartup() =>
        Assert.Equal(App.UiFaultAction.FailStartup,
            App.DecideUiFault(new InvalidOperationException("No service for type X has been registered."),
                              mainWindowLoaded: false));

    /// <summary>A null exception before the window loaded is still a startup fault, not a swallow.</summary>
    /// <remarks>
    /// Defensive: the handler must reach a decision rather than throwing inside itself while reporting a
    /// fault. Null cannot match any of the typed swallows, so it falls through to the window check.
    /// </remarks>
    [Fact]
    public void ANullExceptionIsDecidedByTheWindowStateAlone()
    {
        Assert.Equal(App.UiFaultAction.FailStartup, App.DecideUiFault(null, mainWindowLoaded: false));
        Assert.Equal(App.UiFaultAction.ShowAndKeepRunning, App.DecideUiFault(null, mainWindowLoaded: true));
    }

    /// <summary>
    /// The flag starts false, so a fault that arrives before anything sets it fails startup.
    /// </summary>
    /// <remarks>
    /// The default matters as much as the setter: the handler is wired before the DI build, the tray, the
    /// theme and the window, and everything in that stretch must be treated as a startup fault. Asserted
    /// through the decision rather than by reading the property, since that is how it is used.
    /// </remarks>
    [Fact]
    public void TheWindowIsNotLoadedUntilSomethingSaysSo() =>
        Assert.Equal(App.UiFaultAction.FailStartup,
            App.DecideUiFault(new InvalidOperationException("during startup"), App.MainWindowLoaded));
}
