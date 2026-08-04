// SysManager · NetworkTabUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using FlaUI.Core.AutomationElements;

namespace SysManager.UITests;

/// <summary>
/// UI tests for the Ping tab (Network group). Ping, Traceroute, and Speed Test are now
/// separate sidebar entries (nav-ping / nav-traceroute / nav-speed-test), so this suite
/// only asserts the Ping tab's own content — the old combined "Network" tab with sub-tabs
/// no longer exists.
/// </summary>
[Collection("App")]
public class NetworkTabUiTests
{
    private readonly AppFixture _fx;
    public NetworkTabUiTests(AppFixture fx) => _fx = fx;

    private void GoTo() => _fx.GoToTab("nav-ping");

    [Fact]
    public void Header_Visible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Ping"));
    }

    [Fact]
    public void Subtitle_Visible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Live ping"));
    }

    [Fact]
    public void TargetsCardVisible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Targets"));
    }

    [Fact]
    public void PresetSelectorVisible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Preset"));
    }

    [Fact]
    public void Start_OrStop_Visible()
    {
        GoTo();
        // Either Start (not monitoring) or Stop (monitoring) is shown.
        var start = _fx.FindButtonById("btn-ping-start", timeoutSeconds: 1);
        var stop = _fx.FindButtonById("btn-ping-stop", timeoutSeconds: 1);
        Assert.True(start != null || stop != null);
    }

    [Fact]
    public void ClearButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButtonById("btn-ping-clear"));
    }

    [Fact]
    public void StartStop_Cycle()
    {
        GoTo();
        var completed = false;
        try
        {
            var monitoringBeforeTest = _fx.FindButtonById("btn-ping-stop", timeoutSeconds: 1);
            monitoringBeforeTest?.Invoke();

            var start = _fx.FindButtonById("btn-ping-start");
            Assert.NotNull(start);
            start!.Invoke();

            var stop = _fx.FindButtonById("btn-ping-stop");
            Assert.NotNull(stop);
            stop!.Invoke();

            Assert.NotNull(_fx.FindButtonById("btn-ping-start"));
            Assert.Null(_fx.FindButtonById("btn-ping-stop", timeoutSeconds: 1));
            completed = true;
        }
        finally
        {
            // Keep the shared fixture stopped without replacing the primary test failure.
            try
            {
                if (!completed)
                {
                    var cleanupStop = _fx.FindButtonById("btn-ping-stop", timeoutSeconds: 1)
                        ?? _fx.FindButtonByAccessibleName("Stop ping monitoring");
                    cleanupStop?.Invoke();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Ping cleanup failed without replacing the test result: {ex.Message}");
            }
        }
    }

    [Fact]
    public void HealthHeadline_Renders()
    {
        GoTo();
        // Before Start, the health headline is either "Waiting for data…" or a verdict
        // from a previous run. Either way a real headline element must exist.
        var headline = _fx.WaitForText("data") ?? _fx.WaitForText("healthy") ?? _fx.WaitForText("problem");
        Assert.True(headline != null, "No health headline visible");
    }

    [Fact]
    public void AvgPingMetric_LabelVisible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("AVG PING"));
    }

    [Fact]
    public void WorstLossMetric_LabelVisible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("WORST LOSS"));
    }

    [Fact]
    public void WorstJitterMetric_LabelVisible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("WORST JITTER"));
    }

    [Fact]
    public void AddTargetButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButtonById("btn-ping-add-target"));
    }
}
