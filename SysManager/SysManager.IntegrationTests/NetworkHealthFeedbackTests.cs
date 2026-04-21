using System.Reflection;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class NetworkHealthFeedbackTests
{
    private static void InvokeOnSample(NetworkViewModel vm, PingSample s)
    {
        var m = typeof(NetworkViewModel).GetMethod("OnSample", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(vm, new object[] { s });
    }

    [Fact]
    public void Health_InitialState_IsUnknown()
    {
        var vm = new NetworkViewModel();
        Assert.Equal(HealthVerdict.Unknown, vm.Health.Verdict);
    }

    [Fact]
    public void Health_AfterOneGoodSample_IsNotUnknown()
    {
        var vm = new NetworkViewModel();
        var host = vm.Targets.First(t => !t.IsCustom).Host;
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, 10, "OK"));
        Assert.NotEqual(HealthVerdict.Unknown, vm.Health.Verdict);
    }

    [Fact]
    public void Health_GatewayLossEvent_YieldsLocalNetwork()
    {
        var vm = new NetworkViewModel();
        var gateway = vm.Targets.FirstOrDefault(t => t.Role == TargetRole.Gateway);
        if (gateway == null) return; // no gateway detected

        for (int i = 0; i < 20; i++)
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, gateway.Host, null, "Timeout"));

        Assert.Equal(HealthVerdict.LocalNetwork, vm.Health.Verdict);
    }

    [Fact]
    public void Health_AllClean_YieldsGood()
    {
        var vm = new NetworkViewModel();
        foreach (var t in vm.Targets)
        {
            for (int i = 0; i < 20; i++)
                InvokeOnSample(vm, new PingSample(DateTime.UtcNow, t.Host, 10, "OK"));
        }
        Assert.Equal(HealthVerdict.Good, vm.Health.Verdict);
    }

    [Fact]
    public void Health_ClearHistory_ResetsToUnknown()
    {
        var vm = new NetworkViewModel();
        var host = vm.Targets.First().Host;
        for (int i = 0; i < 10; i++)
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, 10, "OK"));
        Assert.NotEqual(HealthVerdict.Unknown, vm.Health.Verdict);

        vm.ClearHistoryCommand.Execute(null);
        Assert.Equal(HealthVerdict.Unknown, vm.Health.Verdict);
    }

    [Fact]
    public void Health_WorstLoss_TracksMaxAcrossTargets()
    {
        var vm = new NetworkViewModel();
        var hosts = vm.Targets.Take(2).Select(t => t.Host).ToArray();

        for (int i = 0; i < 10; i++)
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, hosts[0], 10, "OK"));

        // Host 1: 50% loss
        for (int i = 0; i < 5; i++)
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, hosts[1], 10, "OK"));
        for (int i = 0; i < 5; i++)
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, hosts[1], null, "Timeout"));

        Assert.Equal(50, vm.Health.WorstLossPercent);
    }

    [Fact]
    public void Health_DisableBadTarget_RemovesItsInfluence()
    {
        var vm = new NetworkViewModel();
        var gateway = vm.Targets.FirstOrDefault(t => t.Role == TargetRole.Gateway);
        if (gateway == null) return;

        for (int i = 0; i < 20; i++)
            InvokeOnSample(vm, new PingSample(DateTime.UtcNow, gateway.Host, null, "Timeout"));
        Assert.Equal(HealthVerdict.LocalNetwork, vm.Health.Verdict);

        gateway.IsEnabled = false;
        Assert.NotEqual(HealthVerdict.LocalNetwork, vm.Health.Verdict);
    }

    [Fact]
    public void Health_ColorHex_IsAlwaysValidCssHex()
    {
        var vm = new NetworkViewModel();
        foreach (var preset in vm.Presets)
        {
            vm.SelectedPreset = preset;
            foreach (var t in vm.Targets.Take(3))
                InvokeOnSample(vm, new PingSample(DateTime.UtcNow, t.Host, 10, "OK"));
            Assert.Matches("^#[0-9A-Fa-f]{6}$", vm.Health.ColorHex);
        }
    }

    [Fact]
    public void Health_DetailAlwaysPopulated_AfterFirstSample()
    {
        var vm = new NetworkViewModel();
        var host = vm.Targets.First().Host;
        InvokeOnSample(vm, new PingSample(DateTime.UtcNow, host, 10, "OK"));
        Assert.False(string.IsNullOrWhiteSpace(vm.Health.Headline));
        Assert.False(string.IsNullOrWhiteSpace(vm.Health.Detail));
    }
}
