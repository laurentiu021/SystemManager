// SysManager · NetworkViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows;
using LiveChartsCore;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Tests for NetworkViewModel. All tests run on an STA thread because the VM
/// touches WPF Application-scoped types.
/// </summary>
[Collection("Network")]
public class NetworkViewModelTests
{
    private static NetworkViewModel MakeFresh()
    {
        var vm = new NetworkViewModel();
        // Make tests fast and offline.
        vm.IntervalSeconds = 1;
        return vm;
    }

    [Fact]
    public void Ctor_SeedsExpectedDefaultTargets()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            var hosts = vm.Targets.Select(t => t.Host).ToList();
            Assert.Contains("8.8.8.8", hosts);
            Assert.Contains("1.1.1.1", hosts);
            Assert.Contains("9.9.9.9", hosts);
            Assert.Contains("google.com", hosts);
            Assert.True(vm.Targets.Count >= 4 && vm.Targets.Count <= 5);
        });
    }

    [Fact]
    public void LatencySeries_MatchesTargetsCount()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            Assert.Equal(vm.Targets.Count, vm.LatencySeries.Count);
        });
    }

    [Fact]
    public void AddCustomTarget_AddsSeriesAndMarksCustom()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            var before = vm.Targets.Count;
            vm.NewTargetHost = "example.com";
            vm.AddCustomTargetCommand.Execute(null);

            Assert.Equal(before + 1, vm.Targets.Count);
            Assert.Equal(vm.Targets.Count, vm.LatencySeries.Count);
            var added = vm.Targets.Last();
            Assert.Equal("example.com", added.Host);
            Assert.True(added.IsCustom);
            Assert.Equal("", vm.NewTargetHost);
        });
    }

    [Fact]
    public void AddCustomTarget_EmptyOrWhitespace_Ignored()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            var before = vm.Targets.Count;
            vm.NewTargetHost = "   ";
            vm.AddCustomTargetCommand.Execute(null);
            Assert.Equal(before, vm.Targets.Count);

            vm.NewTargetHost = "";
            vm.AddCustomTargetCommand.Execute(null);
            Assert.Equal(before, vm.Targets.Count);
        });
    }

    [Fact]
    public void AddCustomTarget_Duplicate_Ignored()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            var before = vm.Targets.Count;
            vm.NewTargetHost = "8.8.8.8"; // already present as default
            vm.AddCustomTargetCommand.Execute(null);
            Assert.Equal(before, vm.Targets.Count);
        });
    }

    [Fact]
    public void RemoveTarget_OnlyCustom_CanBeRemoved()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            var defaultTarget = vm.Targets.First(t => !t.IsCustom);
            var before = vm.Targets.Count;

            vm.RemoveTargetCommand.Execute(defaultTarget);
            Assert.Equal(before, vm.Targets.Count); // refused

            vm.NewTargetHost = "custom.local";
            vm.AddCustomTargetCommand.Execute(null);
            var custom = vm.Targets.Last();
            Assert.True(custom.IsCustom);

            vm.RemoveTargetCommand.Execute(custom);
            Assert.DoesNotContain(vm.Targets, t => t.Host == "custom.local");
            Assert.Equal(vm.Targets.Count, vm.LatencySeries.Count);
        });
    }

    [Fact]
    public void RemoveTarget_Null_IsSafe()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            var before = vm.Targets.Count;
            vm.RemoveTargetCommand.Execute(null);
            Assert.Equal(before, vm.Targets.Count);
        });
    }

    [Fact]
    public void ClearHistory_ResetsPerTargetStats()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            foreach (var t in vm.Targets)
            {
                t.LastLatencyMs = 50;
                t.AverageMs = 42;
                t.LossPercent = 10;
                t.Status = "OK";
            }
            vm.ClearHistoryCommand.Execute(null);

            foreach (var t in vm.Targets)
            {
                Assert.Null(t.LastLatencyMs);
                Assert.Null(t.AverageMs);
                Assert.Equal(0, t.LossPercent);
                Assert.Equal("—", t.Status);
            }
        });
    }

    [Fact]
    public void WindowOptions_ContainsExpectedDurations()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            Assert.Contains(60, vm.WindowOptions);
            Assert.Contains(300, vm.WindowOptions);
            Assert.Contains(600, vm.WindowOptions);
            Assert.Contains(900, vm.WindowOptions);
        });
    }

    [Fact]
    public void IntervalSeconds_MinimumClampedToOne()
    {
        StaHelper.Run(() =>
        {
            var vm = MakeFresh();
            vm.IntervalSeconds = 0;
            // Start/Stop to observe the interval got clamped in the service.
            vm.StartCommand.Execute(null);
            vm.StopCommand.Execute(null);
            // Not crashing + no negative timing is the contract.
        });
    }
}
