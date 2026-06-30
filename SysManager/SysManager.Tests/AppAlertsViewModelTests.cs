// SysManager · AppAlertsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

public class AppAlertsViewModelTests
{
    [Fact]
    public void InitialState_IsCorrect()
    {
        var vm = new AppAlertsViewModel(new Services.AppAlertService());
        Assert.False(vm.IsMonitoring);
        Assert.Equal(0, vm.AlertCount);
        Assert.Equal(0, vm.UnacknowledgedCount);
        Assert.Contains("Start", vm.MonitorStatus);
    }

    [Fact]
    public void AcknowledgeAll_SetsAllAcknowledged()
    {
        var vm = new AppAlertsViewModel(new Services.AppAlertService());
        vm.Alerts.Add(new AppInstallEntry { Name = "App1", IsAcknowledged = false });
        vm.Alerts.Add(new AppInstallEntry { Name = "App2", IsAcknowledged = false });

        vm.AcknowledgeAllCommand.Execute(null);

        Assert.All(vm.Alerts, a => Assert.True(a.IsAcknowledged));
        Assert.Equal(0, vm.UnacknowledgedCount);
    }

    [Fact]
    public void ClearHistory_RemovesAllAlerts()
    {
        var vm = new AppAlertsViewModel(new Services.AppAlertService());
        vm.Alerts.Add(new AppInstallEntry { Name = "App1" });
        vm.Alerts.Add(new AppInstallEntry { Name = "App2" });

        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.Alerts);
        Assert.Equal(0, vm.AlertCount);
    }

    [Fact]
    public async Task RefreshInstalledApps_WhenNotMonitoring_LeavesBusyOff()
    {
        using var vm = new AppAlertsViewModel(new Services.AppAlertService());

        await vm.RefreshInstalledAppsCommand.ExecuteAsync(null);

        Assert.False(vm.IsMonitoring);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RefreshInstalledApps_WhileMonitoring_KeepsBusyInSyncWithMonitoring()
    {
        // Regression (idx 235): a manual refresh must not switch off the busy/monitoring
        // affordance while monitoring is active — IsBusy has to track IsMonitoring.
        using var vm = new AppAlertsViewModel(new Services.AppAlertService());
        vm.StartMonitoringCommand.Execute(null);
        Assert.True(vm.IsMonitoring);
        Assert.True(vm.IsBusy);

        await vm.RefreshInstalledAppsCommand.ExecuteAsync(null);

        Assert.True(vm.IsMonitoring);
        Assert.True(vm.IsBusy); // was incorrectly forced to false before the fix
    }

    [Fact]
    public void AppInstallEntry_DefaultValues()
    {
        var entry = new AppInstallEntry();
        Assert.Equal("", entry.Name);
        Assert.Equal("", entry.Publisher);
        Assert.Equal("", entry.InstallPath);
        Assert.Equal("", entry.Source);
        Assert.False(entry.IsAcknowledged);
    }

    [Fact]
    public void AppInstallEntry_PropertyChanged_Fires()
    {
        var entry = new AppInstallEntry();
        string? changed = null;
        entry.PropertyChanged += (_, e) => changed = e.PropertyName;

        entry.Name = "TestApp";
        Assert.Equal("Name", changed);

        entry.IsAcknowledged = true;
        Assert.Equal("IsAcknowledged", changed);
    }
}
