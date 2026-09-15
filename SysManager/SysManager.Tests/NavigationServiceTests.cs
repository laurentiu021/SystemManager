// SysManager · NavigationServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The navigation seam that lets a tab which diagnoses something offer the tab that fixes it (#1496,
/// #1504).
/// </summary>
/// <remarks>
/// The behaviour these tests cover was previously unreachable from a test at all: the one caller reached
/// through <c>Application.Current.MainWindow.DataContext</c> and cast it to the shell, so "does clicking
/// this send the user to the right tab" could only be answered by running the app. That is the whole
/// reason the seam came before the two features.
/// </remarks>
public class NavigationServiceTests
{
    private static (NavigationService Service, INavigationTarget Shell) Bound()
    {
        var shell = Substitute.For<INavigationTarget>();
        var service = new NavigationService();
        service.Bind(shell);
        return (service, shell);
    }

    [Fact]
    public void GoTo_ForwardsTheNavIdToTheShell()
    {
        var (service, shell) = Bound();

        service.GoTo("nav-system-health");

        shell.Received(1).NavigateTo("nav-system-health", null);
    }

    [Fact]
    public void GoTo_ForwardsTheFilterWhenOneIsGiven()
    {
        var (service, shell) = Bound();

        service.GoTo("nav-services", "Spooler");

        shell.Received(1).NavigateTo("nav-services", "Spooler");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GoTo_IgnoresAnEmptyNavId(string navId)
    {
        // Every caller reads the id off a model where empty means "nothing here can act on this" — a green
        // alert, a worn battery, a slow driver. Forwarding it would ask the shell to select nothing.
        var (service, shell) = Bound();

        service.GoTo(navId);

        shell.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
    }

    [Fact]
    public void GoTo_BeforeTheShellBinds_DoesNothingRatherThanThrowing()
    {
        // Unbound happens in a test or the designer. An exception here would fail unrelated tests for a
        // navigation nobody was watching; swallowing it is right, and the Debug log says it happened.
        var service = new NavigationService();

        service.GoTo("nav-system-health");   // must not throw
    }

    [Fact]
    public void Bind_ReplacesAnEarlierShell()
    {
        // The shell is re-created when the window is, and the service is a singleton that outlives it.
        // Holding the first one would navigate a dead window for the rest of the session.
        var first = Substitute.For<INavigationTarget>();
        var second = Substitute.For<INavigationTarget>();
        var service = new NavigationService();

        service.Bind(first);
        service.Bind(second);
        service.GoTo("nav-logs");

        second.Received(1).NavigateTo("nav-logs", null);
        first.DidNotReceiveWithAnyArgs().NavigateTo(default!, default);
    }

    // ---- The routing rules the two features depend on ----

    [Theory]
    [InlineData(AlertSeverity.Green, "")]
    [InlineData(AlertSeverity.Yellow, "nav-system-health")]
    [InlineData(AlertSeverity.Red, "nav-system-health")]
    public void NavTargetFor_OffersARouteOnlyWhenSomethingIsWrong(AlertSeverity severity, string expected)
    {
        // "Fix this" beside "All SMART indicators healthy" would teach the user the button means nothing.
        Assert.Equal(expected,
            ViewModels.DashboardViewModel.NavTargetFor(severity, "nav-system-health"));
    }

    [Theory]
    [InlineData("Service", "nav-services", "Open Services")]
    [InlineData("Application", "nav-startup", "Manage startup items")]
    [InlineData("Background", "nav-startup", "Manage startup items")]
    public void BootDegradation_RoutesByWhatKindOfComponentItIs(string kind, string navId, string label)
    {
        var row = new BootDegradation(DateTime.UtcNow, kind, "Something", 4200);

        Assert.Equal(navId, row.NavTargetId);
        Assert.Equal(label, row.NavLabel);
        Assert.True(row.CanNavigate);
    }

    [Theory]
    [InlineData("Driver")]
    [InlineData("Device")]
    [InlineData("Something new Windows started reporting")]
    public void BootDegradation_OffersNothingForComponentsNoTabCanTouch(string kind)
    {
        // No tab in this app disables a driver. Sending someone to Startup Manager to hunt for one they
        // will not find is worse than showing no link at all.
        var row = new BootDegradation(DateTime.UtcNow, kind, "Something", 4200);

        Assert.Equal("", row.NavTargetId);
        Assert.Equal("", row.NavLabel);
        Assert.False(row.CanNavigate);
    }

    [Fact]
    public void DashboardAlert_CanNavigate_TracksItsTarget()
    {
        // The view binds Visibility to CanNavigate, and the target is assigned AFTER the alert is created
        // — on the dispatcher, once its scan finishes. Without the change notification the button would
        // stay hidden for every finding.
        var alert = new DashboardAlert();
        var raised = alert.RecordPropertyChanges();

        Assert.False(alert.CanNavigate);

        alert.NavTargetId = "nav-logs";

        Assert.True(alert.CanNavigate);
        Assert.Contains(nameof(DashboardAlert.CanNavigate), raised);
    }
}
