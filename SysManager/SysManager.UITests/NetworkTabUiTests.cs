// SysManager · NetworkTabUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using FlaUI.Core.AutomationElements;

namespace SysManager.UITests;

[Collection("App")]
public class NetworkTabUiTests
{
    private readonly AppFixture _fx;
    public NetworkTabUiTests(AppFixture fx) => _fx = fx;

    private void GoTo() => _fx.GoToTab("nav-network");

    [Fact]
    public void Header_Visible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Network"));
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
    public void SubTabs_Ping_Traceroute_SpeedTest_Visible()
    {
        GoTo();
        Assert.NotNull(_fx.WaitForText("Ping"));
        Assert.NotNull(_fx.WaitForText("Traceroute"));
        Assert.NotNull(_fx.WaitForText("Speed test"));
    }

    [Fact]
    public void Start_OrStop_Visible()
    {
        GoTo();
        // Either Start (not monitoring) or Stop (monitoring) is shown.
        var start = _fx.FindButton("Start");
        var stop = _fx.FindButton("Stop");
        Assert.True(start != null || stop != null);
    }

    [Fact]
    public void ClearButton_Exists()
    {
        GoTo();
        Assert.NotNull(_fx.FindButton("Clear"));
    }

    [Fact]
    public void StartStop_Cycle()
    {
        GoTo();
        var start = _fx.FindButton("Start");
        if (start != null)
        {
            start.Invoke();
            Thread.Sleep(400);
        }
        var stop = _fx.FindButton("Stop");
        Assert.NotNull(stop);
        stop!.Invoke();
        Thread.Sleep(400);
    }

    [Fact]
    public void HealthHeadline_Renders()
    {
        GoTo();
        // Before Start, the health headline is either "Waiting for data…" or a
        // verdict from a previous run. Either way, the word "…" / a real headline
        // element must exist.
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
        Assert.NotNull(_fx.FindButton("Add target"));
    }

    [Fact]
    public void SwitchToTraceroute_ShowsTraceNow()
    {
        GoTo();
        // Click the Traceroute pill
        var pill = _fx.MainWindow.FindAllDescendants()
            .FirstOrDefault(e =>
                string.Equals(e.Name, "Traceroute", StringComparison.OrdinalIgnoreCase) &&
                e.ControlType == FlaUI.Core.Definitions.ControlType.TabItem);
        if (pill != null)
        {
            pill.AsTabItem().Select();
            Thread.Sleep(300);
            Assert.NotNull(_fx.WaitForText("Trace now"));
        }
    }

    [Fact]
    public void SwitchToSpeedTest_ShowsBothEngines()
    {
        GoTo();
        var pill = _fx.MainWindow.FindAllDescendants()
            .FirstOrDefault(e =>
                string.Equals(e.Name, "Speed test", StringComparison.OrdinalIgnoreCase) &&
                e.ControlType == FlaUI.Core.Definitions.ControlType.TabItem);
        if (pill != null)
        {
            pill.AsTabItem().Select();
            Thread.Sleep(300);
            Assert.NotNull(_fx.WaitForText("HTTP speed test"));
            Assert.NotNull(_fx.WaitForText("Ookla speed test"));
        }
    }
}
