// SysManager · NetworkExhaustiveTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Broad exhaustive coverage for the Network tab's VM — preset switching,
/// loss accounting, target lifecycle under load, interval clamping, etc.
/// </summary>
[Collection("Network")]
public class NetworkExhaustiveTests
{
    private static void InvokeOnSample(NetworkViewModel vm, PingSample sample)
    {
        var m = typeof(NetworkViewModel).GetMethod("OnSample", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(vm, new object[] { sample });
    }

    // ---------- Preset switching ----------

    [Fact]
    public void SwitchingPreset_ReplacesNonGatewayTargets()
    {
        var vm = new NetworkViewModel();
        var initialCount = vm.Targets.Count;
        Assert.Contains(vm.Targets, t => t.Host == "8.8.8.8");

        vm.SelectedPreset = TargetPresets.CS2Europe;
        Assert.Contains(vm.Targets, t => t.Host == "146.66.155.1");
        Assert.DoesNotContain(vm.Targets, t => t.Host == "8.8.8.8");

        vm.SelectedPreset = TargetPresets.PubgEurope;
        Assert.Contains(vm.Targets, t => t.Host.Contains("amazonaws"));
        Assert.DoesNotContain(vm.Targets, t => t.Host == "146.66.155.1");
    }

    [Fact]
    public void SwitchingPreset_PreservesCustomTargets()
    {
        var vm = new NetworkViewModel();
        vm.NewTargetHost = "my.custom.host";
        vm.AddCustomTargetCommand.Execute(null);

        vm.SelectedPreset = TargetPresets.CS2Europe;
        Assert.Contains(vm.Targets, t => t.Host == "my.custom.host");

        vm.SelectedPreset = TargetPresets.PubgEurope;
        Assert.Contains(vm.Targets, t => t.Host == "my.custom.host");
    }

    [Fact]
    public void SwitchingPreset_PreservesGateway()
    {
        var vm = new NetworkViewModel();
        var gateway = vm.Targets.FirstOrDefault(t => t.Role == TargetRole.Gateway);
        if (gateway == null) return; // no gateway detected on this machine
        var host = gateway.Host;

        vm.SelectedPreset = TargetPresets.Streaming;
        Assert.Contains(vm.Targets, t => t.Host == host && t.Role == TargetRole.Gateway);
    }

    [Fact]
    public void PresetTargets_HaveGameServerRole_ForCs2AndPubg()
    {
        var vm = new NetworkViewModel();

        vm.SelectedPreset = TargetPresets.CS2Europe;
        foreach (var t in vm.Targets.Where(x => x.Host.StartsWith("146.66.") || x.Host.StartsWith("155.133.")))
            Assert.Equal(TargetRole.GameServer, t.Role);

        vm.SelectedPreset = TargetPresets.PubgEurope;
        foreach (var t in vm.Targets.Where(x => x.Host.Contains("amazonaws")))
            Assert.Equal(TargetRole.GameServer, t.Role);
    }

    [Fact]
    public void StreamingPreset_SetsStreamingRole()
    {
        var vm = new NetworkViewModel();
        vm.SelectedPreset = TargetPresets.Streaming;
        Assert.Contains(vm.Targets, t => t.Host == "youtube.com" && t.Role == TargetRole.Streaming);
        Assert.Contains(vm.Targets, t => t.Host == "twitch.tv" && t.Role == TargetRole.Streaming);
    }

    // ---------- Loss accounting ----------

    [Fact]
    public void AllTimeouts_ReportLoss100AndNullAverage()
    {
        var vm = new NetworkViewModel();
        var host = vm.Targets.First().Host;

        for (int i = 0; i < 5; i++)
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, null, "Timeout"));

        var target = vm.Targets.First(t => t.Host == host);
        Assert.Equal(100, target.LossPercent);
        Assert.Null(target.AverageMs);
        Assert.Null(target.JitterMs);
        Assert.Equal("Timeout", target.Status);
    }

    [Fact]
    public void MixedSamples_ReportCorrectLoss()
    {
        var vm = new NetworkViewModel();
        var host = vm.Targets.First().Host;

        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, 10, "OK"));
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, null, "Timeout"));
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, 20, "OK"));
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, null, "Timeout"));

        var target = vm.Targets.First(t => t.Host == host);
        Assert.Equal(50, target.LossPercent);
        Assert.Equal(15, target.AverageMs);
    }

    // ---------- Commands ----------

    [Fact]
    public void StartCommand_FlipsIsMonitoring()
    {
        var vm = new NetworkViewModel();
        Assert.False(vm.IsMonitoring);
        vm.StartCommand.Execute(null);
        Assert.True(vm.IsMonitoring);
        vm.StopCommand.Execute(null);
        Assert.False(vm.IsMonitoring);
    }

    [Fact]
    public void ClearHistory_ResetsEverything()
    {
        var vm = new NetworkViewModel();
        var target = vm.Targets.First();
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, target.Host, 20, "OK"));
        Assert.NotNull(target.LastLatencyMs);

        vm.ClearHistoryCommand.Execute(null);
        Assert.Null(target.LastLatencyMs);
        Assert.Null(target.AverageMs);
        Assert.Equal(0, target.LossPercent);
    }

    [Fact]
    public void AddCustomTarget_EmptyOrDuplicate_NoOp()
    {
        var vm = new NetworkViewModel();
        var before = vm.Targets.Count;

        vm.NewTargetHost = "";
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(before, vm.Targets.Count);

        vm.NewTargetHost = "   ";
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(before, vm.Targets.Count);

        var existing = vm.Targets.First().Host;
        vm.NewTargetHost = existing;
        vm.AddCustomTargetCommand.Execute(null);
        Assert.Equal(before, vm.Targets.Count);
    }

    [Fact]
    public void RemoveTarget_WontRemoveBuiltIns()
    {
        var vm = new NetworkViewModel();
        var builtIn = vm.Targets.First(t => !t.IsCustom);
        var before = vm.Targets.Count;
        vm.RemoveTargetCommand.Execute(builtIn);
        Assert.Equal(before, vm.Targets.Count);
    }

    // ---------- Interval/Window clamps ----------

    [Fact]
    public void IntervalSeconds_AcceptsNegative_ButServiceClamps()
    {
        var vm = new NetworkViewModel();
        vm.IntervalSeconds = -5;
        vm.StartCommand.Execute(null);
        vm.StopCommand.Execute(null);
        // Should not throw or hang.
    }

    [Fact]
    public void TraceIntervalSeconds_AcceptsTooSmall_ButServiceClamps()
    {
        var vm = new NetworkViewModel();
        vm.TraceIntervalSeconds = 1;
        vm.StartCommand.Execute(null);
        vm.StopCommand.Execute(null);
    }

    // ---------- Buffer cleanup on disable ----------

    [Fact]
    public void DisableTarget_KeepsOthersUntouched()
    {
        var vm = new NetworkViewModel();
        var t1 = vm.Targets.First(t => t.Host == "8.8.8.8");
        var t2 = vm.Targets.First(t => t.Host == "1.1.1.1");

        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "8.8.8.8", 10, "OK"));
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, "1.1.1.1", 20, "OK"));
        Assert.Equal(10, t1.LastLatencyMs);
        Assert.Equal(20, t2.LastLatencyMs);

        t1.IsEnabled = false;
        Assert.Null(t1.LastLatencyMs);
        // t2 must be unaffected.
        Assert.Equal(20, t2.LastLatencyMs);
    }
}
