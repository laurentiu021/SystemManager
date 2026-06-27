// SysManager · PerformanceUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;

namespace SysManager.UITests;

/// <summary>
/// Coarse performance / responsiveness guards. These assert generous upper
/// bounds — they exist to catch a regression that makes the app hang or a tab
/// take pathologically long to render, NOT to benchmark. Bounds are deliberately
/// loose so a slow CI runner doesn't cause false failures (the real signal is
/// "seconds, not minutes"). Wall-clock timing is acceptable here because we're
/// asserting an upper bound on a user-facing latency, not testing logic.
/// </summary>
[Collection("App")]
public class PerformanceUiTests
{
    private readonly AppFixture _fx;
    public PerformanceUiTests(AppFixture fx) => _fx = fx;

    [Fact]
    public void TabNavigation_RendersHeader_WithinBudget()
    {
        // Switching to a content-heavy tab must surface its header well within a
        // few seconds. 10s is a generous ceiling for a cold CI runner.
        var sw = Stopwatch.StartNew();
        _fx.GoToTab("nav-logs");
        var header = _fx.WaitForText("System Logs", timeoutSeconds: 10);
        sw.Stop();

        Assert.NotNull(header);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Navigating to System Logs took {sw.Elapsed.TotalSeconds:F1}s — over the 10s budget.");
    }

    [Fact]
    public void RepeatedNavigation_StaysResponsive()
    {
        // 20 tab switches across light + heavy tabs must complete without the app
        // becoming unresponsive or exiting. Catches a leak/deadlock that degrades
        // navigation over time.
        var sw = Stopwatch.StartNew();
        string[] cycle = { "nav-dashboard", "nav-services", "nav-logs", "nav-ping" };
        for (var i = 0; i < 20; i++)
            _fx.GoToTab(cycle[i % cycle.Length]);
        sw.Stop();

        Assert.False(_fx.App.HasExited, "App exited during repeated navigation.");
        Assert.True(_fx.HasText("Ping") || _fx.HasText("Dashboard"),
            "App stopped rendering tab content after repeated navigation.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
            $"20 tab switches took {sw.Elapsed.TotalSeconds:F1}s — over the 60s budget (possible degradation).");
    }

    [Fact]
    public void MainWindow_StaysResponsive_NotHung()
    {
        // A hung UI thread shows up as an unresponsive window. After exercising a
        // tab, the main window must still answer UI Automation queries promptly.
        _fx.GoToTab("nav-dashboard");
        var sw = Stopwatch.StartNew();
        var title = _fx.MainWindow.Title;
        sw.Stop();

        Assert.Contains("SysManager", title, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Reading the main window title took {sw.Elapsed.TotalSeconds:F1}s — UI thread may be blocked.");
    }
}
