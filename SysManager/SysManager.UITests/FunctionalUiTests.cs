// SysManager · FunctionalUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using FlaUI.Core.AutomationElements;

namespace SysManager.UITests;

/// <summary>
/// Functional UI tests: invoke SAFE, read-only actions and assert the
/// observable effect, not just that a control exists. Only non-destructive,
/// non-elevating operations are exercised here (scans, refreshes, list loads) —
/// nothing that changes system state, deletes files, or requires admin. Each
/// test drives the real app through UI Automation exactly as a user would.
/// </summary>
[Collection("App")]
public class FunctionalUiTests
{
    private readonly AppFixture _fx;
    public FunctionalUiTests(AppFixture fx) => _fx = fx;

    [Fact]
    public void Services_Refresh_PopulatesList()
    {
        _fx.GoToTab("nav-services");
        var refresh = _fx.FindButton("Refresh");
        Assert.NotNull(refresh);
        refresh!.Invoke();

        // A populated services list shows the "running / total" summary with a
        // non-zero total — every Windows box has dozens of services. Give the
        // background scan a few seconds to complete.
        var ok = FlaUI.Core.Tools.Retry.WhileFalse(
            () =>
            {
                var totalText = _fx.WaitForText("total", 1);
                // The summary reads e.g. "120 running / 250 total"; assert it isn't "0 total".
                return totalText is not null
                    && _fx.MainWindow.FindAllDescendants()
                        .Any(e => !string.IsNullOrEmpty(e.Name)
                                  && e.Name.Contains("total", StringComparison.OrdinalIgnoreCase)
                                  && !e.Name.TrimStart().StartsWith("0 ", StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(15)).Success;

        Assert.True(ok, "Services list did not populate with a non-zero total after Refresh.");
    }

    [Fact]
    public void Logs_Refresh_LoadsEventsOrReportsState()
    {
        _fx.GoToTab("nav-logs");
        var refresh = _fx.FindButton("Refresh");
        Assert.NotNull(refresh);
        refresh!.Invoke();

        // After a refresh the status bar resolves to a terminal state: either
        // "Loaded N events …" on success, or a clear access/error message. Any of
        // these proves the command ran to completion rather than hanging.
        var resolved = FlaUI.Core.Tools.Retry.WhileNull(
            () => _fx.WaitForText("Loaded", 1)
                  ?? _fx.WaitForText("Access denied", 1)
                  ?? _fx.WaitForText("Event log error", 1),
            TimeSpan.FromSeconds(20)).Result;

        Assert.NotNull(resolved);
    }

    [Fact]
    public void Drivers_List_ProducesCountOrDone()
    {
        _fx.GoToTab("nav-drivers");
        var list = _fx.FindButton("List drivers");
        Assert.NotNull(list);
        list!.Invoke();

        // The scan ends with a "<N> drivers found" summary (or a parse-error
        // fallback). Either terminal message proves the action completed.
        var done = FlaUI.Core.Tools.Retry.WhileNull(
            () => _fx.WaitForText("drivers found", 1)
                  ?? _fx.WaitForText("Parse error", 1),
            TimeSpan.FromSeconds(25)).Result;

        Assert.NotNull(done);
    }

    [Fact]
    public void DiskAnalyzer_ShowsReadOnlyEmptyState_BeforeScan()
    {
        // Read-only tab: before analyzing anything it must show its neutral
        // empty-state guidance, never a stale or error state.
        _fx.GoToTab("nav-disk-analyzer");
        Assert.True(
            _fx.HasText("pick a folder") || _fx.HasText("No results"),
            "Disk Analyzer did not show its pre-scan empty-state message.");
    }

    [Fact]
    public void ProcessManager_AutoPopulates_ProcessList()
    {
        _fx.GoToTab("nav-processes");
        // Process Manager auto-refreshes on a 1s loop; the summary resolves to
        // "<N> processes · <size> total memory" once the scan completes. Match the
        // "total memory" tail so this doesn't trivially pass on the banner text
        // ("Ending system processes requires administrator privileges.").
        var populated = FlaUI.Core.Tools.Retry.WhileNull(
            () => _fx.WaitForText("total memory", 1),
            TimeSpan.FromSeconds(12)).Result;
        Assert.NotNull(populated);
    }

    [Fact]
    public void RapidTabSwitching_DoesNotCrash_AndDashboardRecovers()
    {
        // Stress the navigation: hop across heavy tabs quickly, then confirm the
        // app is still alive and the Dashboard still renders its score.
        foreach (var id in new[] {
            "nav-processes", "nav-logs", "nav-services", "nav-system-health",
            "nav-disk-analyzer", "nav-dns-hosts", "nav-dashboard" })
        {
            _fx.GoToTab(id);
        }
        Assert.False(_fx.App.HasExited, "App exited during rapid tab switching.");
        Assert.True(_fx.HasText("Dashboard"), "Dashboard did not recover after rapid tab switching.");
    }
}
