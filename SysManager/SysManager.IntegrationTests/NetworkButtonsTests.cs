using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Button-level coverage for the Network tab. Every command must be:
///  - safe to call idempotently
///  - safe to call in any order
///  - safe to call before / during / after monitoring
///  - correctly wired to the expected state change
/// </summary>
[Collection("Network")]
public class NetworkButtonsTests
{
    private static void InvokeOnSample(NetworkViewModel vm, PingSample sample)
    {
        var m = typeof(NetworkViewModel).GetMethod("OnSample", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(vm, new object[] { sample });
    }

    // ---------- Start/Stop ----------

    [Fact]
    public void StartCommand_Idempotent()
    {
        var vm = new NetworkViewModel();
        vm.StartCommand.Execute(null);
        vm.StartCommand.Execute(null);
        vm.StartCommand.Execute(null);
        Assert.True(vm.IsMonitoring);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void StopCommand_WithoutStart_IsSafe()
    {
        var vm = new NetworkViewModel();
        var ex = Record.Exception(() => vm.StopCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void StartStop_Interleaved_NoCrash()
    {
        var vm = new NetworkViewModel();
        for (int i = 0; i < 10; i++)
        {
            vm.StartCommand.Execute(null);
            vm.StopCommand.Execute(null);
        }
        Assert.False(vm.IsMonitoring);
    }

    [Fact]
    public void Start_SetsStatusMessage()
    {
        var vm = new NetworkViewModel();
        vm.StartCommand.Execute(null);
        Assert.Contains("monitoring", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void Stop_SetsStatusMessage()
    {
        var vm = new NetworkViewModel();
        vm.StartCommand.Execute(null);
        vm.StopCommand.Execute(null);
        Assert.Contains("stopped", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Clear ----------

    [Fact]
    public void Clear_WithoutAnyData_IsSafe()
    {
        var vm = new NetworkViewModel();
        var ex = Record.Exception(() => vm.ClearHistoryCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void Clear_AfterData_ResetsAllTargetStats()
    {
        var vm = new NetworkViewModel();
        foreach (var t in vm.Targets)
        {
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, t.Host, 10, "OK"));
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, t.Host, 20, "OK"));
        }
        vm.ClearHistoryCommand.Execute(null);
        foreach (var t in vm.Targets)
        {
            Assert.Null(t.LastLatencyMs);
            Assert.Null(t.AverageMs);
            Assert.Equal(0, t.LossPercent);
        }
    }

    [Fact]
    public void Clear_UpdatesHealthDiagnostic()
    {
        var vm = new NetworkViewModel();
        var t = vm.Targets.First();
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, t.Host, 10, "OK"));
        vm.ClearHistoryCommand.Execute(null);
        // Health should not throw; with no samples we expect Unknown or Good.
        Assert.NotNull(vm.Health);
    }

    // ---------- Add custom target ----------

    [Theory]
    [InlineData("example.com")]
    [InlineData("1.2.3.4")]
    [InlineData("ec2.eu-west-1.amazonaws.com")]
    [InlineData("192.168.0.1")]
    public void AddTarget_ValidHost_IsAdded(string host)
    {
        var vm = new NetworkViewModel();
        var before = vm.Targets.Count;
        vm.NewTargetHost = host;
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(before + 1, vm.Targets.Count);
        Assert.Contains(vm.Targets, t => t.Host == host && t.IsCustom);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void AddTarget_WhitespaceOrEmpty_Ignored(string host)
    {
        var vm = new NetworkViewModel();
        var before = vm.Targets.Count;
        vm.NewTargetHost = host;
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(before, vm.Targets.Count);
    }

    [Fact]
    public void AddTarget_TrimWhitespaceAroundInput()
    {
        var vm = new NetworkViewModel();
        vm.NewTargetHost = "   trimmed.example  ";
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Contains(vm.Targets, t => t.Host == "trimmed.example");
    }

    [Fact]
    public void AddTarget_ClearsInputFieldAfterAdd()
    {
        var vm = new NetworkViewModel();
        vm.NewTargetHost = "fresh.example";
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal("", vm.NewTargetHost);
    }

    [Fact]
    public void AddTarget_AlwaysCreatesSeries()
    {
        var vm = new NetworkViewModel();
        var beforeSeries = vm.LatencySeries.Count;
        var beforeTrace = vm.TraceSeries.Count;
        vm.NewTargetHost = "series-check.example";
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(beforeSeries + 1, vm.LatencySeries.Count);
        Assert.Equal(beforeTrace + 1, vm.TraceSeries.Count);
    }

    // ---------- Remove target ----------

    [Fact]
    public void RemoveTarget_Null_NoOp()
    {
        var vm = new NetworkViewModel();
        var before = vm.Targets.Count;
        vm.RemoveTargetCommand.Execute(null);
        Assert.Equal(before, vm.Targets.Count);
    }

    [Fact]
    public void RemoveTarget_Custom_Removed()
    {
        var vm = new NetworkViewModel();
        vm.NewTargetHost = "remove-me.example";
        vm.AddCustomTargetCommand.Execute(null);
        var custom = vm.Targets.Last();
        vm.RemoveTargetCommand.Execute(custom);
        Assert.DoesNotContain(vm.Targets, t => t.Host == "remove-me.example");
    }

    [Fact]
    public void RemoveTarget_BuiltIn_Refused()
    {
        var vm = new NetworkViewModel();
        var builtIn = vm.Targets.First(t => !t.IsCustom);
        var before = vm.Targets.Count;
        vm.RemoveTargetCommand.Execute(builtIn);
        Assert.Equal(before, vm.Targets.Count);
    }

    [Fact]
    public void RemoveTarget_AlsoRemovesSeries()
    {
        var vm = new NetworkViewModel();
        vm.NewTargetHost = "series-remove.example";
        vm.AddCustomTargetCommand.Execute(null);
        var t = vm.Targets.Last();
        var before = vm.LatencySeries.Count;
        vm.RemoveTargetCommand.Execute(t);
        Assert.Equal(before - 1, vm.LatencySeries.Count);
    }

    // ---------- Enable/disable per target ----------

    [Fact]
    public void DisableAllTargets_DoesNotCrash()
    {
        var vm = new NetworkViewModel();
        foreach (var t in vm.Targets) t.IsEnabled = false;
        vm.StartCommand.Execute(null);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void EnableIndividualTargets_Works()
    {
        var vm = new NetworkViewModel();
        foreach (var t in vm.Targets) t.IsEnabled = false;
        var first = vm.Targets.First();
        first.IsEnabled = true;
        Assert.True(first.IsEnabled);
        Assert.All(vm.Targets.Skip(1), t => Assert.False(t.IsEnabled));
    }

    [Fact]
    public void ToggleEach_Target_IsIndependent()
    {
        var vm = new NetworkViewModel();
        for (int i = 0; i < vm.Targets.Count; i++)
        {
            vm.Targets[i].IsEnabled = !vm.Targets[i].IsEnabled;
        }
        for (int i = 0; i < vm.Targets.Count; i++)
        {
            vm.Targets[i].IsEnabled = !vm.Targets[i].IsEnabled;
        }
    }

    // ---------- Presets ----------

    [Theory]
    [InlineData("Global")]
    [InlineData("CS2 Europe")]
    [InlineData("PUBG Europe")]
    [InlineData("Streaming")]
    public void AllPresets_AreSelectable(string name)
    {
        var vm = new NetworkViewModel();
        var preset = vm.Presets.First(p => p.Name == name);
        vm.SelectedPreset = preset;
        Assert.Equal(preset, vm.SelectedPreset);
    }

    [Fact]
    public void AllPresets_ChangingRepeatedly_Works()
    {
        var vm = new NetworkViewModel();
        for (int i = 0; i < 5; i++)
        {
            foreach (var p in vm.Presets)
            {
                vm.SelectedPreset = p;
                Assert.Same(p, vm.SelectedPreset);
                // Some targets must exist
                Assert.NotEmpty(vm.Targets);
            }
        }
    }

    [Fact]
    public void ChangingPreset_KeepsSeriesAndTargetsInSync()
    {
        var vm = new NetworkViewModel();
        foreach (var p in vm.Presets)
        {
            vm.SelectedPreset = p;
            Assert.Equal(vm.Targets.Count, vm.LatencySeries.Count);
            Assert.Equal(vm.Targets.Count, vm.TraceSeries.Count);
        }
    }

    [Fact]
    public void ChangingPreset_WhileMonitoring_DoesNotCrash()
    {
        var vm = new NetworkViewModel();
        vm.StartCommand.Execute(null);
        foreach (var p in vm.Presets)
            vm.SelectedPreset = p;
        vm.StopCommand.Execute(null);
    }

    // ---------- Interval / window ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public void IntervalSeconds_AcceptsNormalRange(int seconds)
    {
        var vm = new NetworkViewModel { IntervalSeconds = seconds };
        Assert.Equal(seconds, vm.IntervalSeconds);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    [InlineData(600)]
    [InlineData(900)]
    public void WindowSeconds_AcceptsAllOptions(int window)
    {
        var vm = new NetworkViewModel { WindowSeconds = window };
        Assert.Equal(window, vm.WindowSeconds);
    }

    [Fact]
    public void WindowShrinks_TrimsOlderEntries()
    {
        var vm = new NetworkViewModel();
        var host = vm.Targets.First().Host;
        vm.WindowSeconds = 600;

        // Push 100 synthetic samples over time
        for (int i = 0; i < 100; i++)
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow.AddSeconds(-i), host, 10, "OK"));

        vm.WindowSeconds = 60; // force trim on next sample
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, 10, "OK"));
        // No exception, stats remain computed
        var target = vm.Targets.First(t => t.Host == host);
        Assert.NotNull(target.AverageMs);
    }

    // ---------- Trace commands ----------

    [Fact]
    public void TraceCommand_EmptyHost_Ignored()
    {
        var vm = new NetworkViewModel();
        vm.TraceHost = "";
        vm.TraceCommand.Execute(null);
        Assert.False(vm.IsTracing);
    }

    [Fact]
    public void CancelTraceCommand_WithoutActiveTrace_IsSafe()
    {
        var vm = new NetworkViewModel();
        var ex = Record.Exception(() => vm.CancelTraceCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void TraceHost_DefaultIsGoogleDns()
    {
        var vm = new NetworkViewModel();
        Assert.Equal("8.8.8.8", vm.TraceHost);
    }

    // ---------- Speed commands ----------

    [Fact]
    public void SpeedCancel_WithoutActiveTest_IsSafe()
    {
        var vm = new NetworkViewModel();
        var ex = Record.Exception(() => vm.CancelSpeedCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void SpeedProgress_DefaultZero()
    {
        var vm = new NetworkViewModel();
        Assert.Equal(0, vm.SpeedProgress);
    }

    [Fact]
    public void SpeedResults_DefaultNull()
    {
        var vm = new NetworkViewModel();
        Assert.Null(vm.HttpResult);
        Assert.Null(vm.OoklaResult);
    }

    // ---------- Repeated add same host protection ----------

    [Fact]
    public void AddingSameCustomHost_Twice_IsNoOp()
    {
        var vm = new NetworkViewModel();
        vm.NewTargetHost = "dup.example";
        vm.AddCustomTargetCommand.Execute(null);
        var after1 = vm.Targets.Count;
        vm.NewTargetHost = "dup.example";
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(after1, vm.Targets.Count);
    }

    [Fact]
    public void AddingCaseSensitiveDifferentHost_IsTreatedSame()
    {
        var vm = new NetworkViewModel();
        vm.NewTargetHost = "Example.com";
        vm.AddCustomTargetCommand.Execute(null);
        vm.NewTargetHost = "example.com";
        vm.AddCustomTargetCommand.Execute(null);
        var count = vm.Targets.Count(t => t.Host.Equals("example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, count);
    }
}
