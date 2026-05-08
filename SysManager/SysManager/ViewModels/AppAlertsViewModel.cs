// SysManager · AppAlertsViewModel — monitors and alerts on new app installations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// App Alerts tab — monitors for new application installations and shows
/// a timestamped history of detected installs.
/// </summary>
public partial class AppAlertsViewModel : ViewModelBase
{
    private readonly AppAlertService _service = new();
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<AppInstallEntry> Alerts { get; } = new();

    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private string _monitorStatus = "Click Start to begin monitoring for new installations.";
    [ObservableProperty] private int _alertCount;
    [ObservableProperty] private int _unacknowledgedCount;

    public AppAlertsViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _service.NewAppDetected += OnNewAppDetected;
    }

    [RelayCommand]
    private void StartMonitoring()
    {
        if (IsMonitoring) return;

        _service.TakeBaseline();
        _service.Start();
        IsMonitoring = true;
        IsBusy = true;
        MonitorStatus = "Monitoring active — watching for new installations...";
        Log.Information("App alert monitoring started by user");
    }

    [RelayCommand]
    private void StopMonitoring()
    {
        if (!IsMonitoring) return;

        _service.Stop();
        IsMonitoring = false;
        IsBusy = false;
        MonitorStatus = $"Monitoring stopped. {AlertCount} alert{(AlertCount == 1 ? "" : "s")} recorded.";
        Log.Information("App alert monitoring stopped by user");
    }

    [RelayCommand]
    private void AcknowledgeAll()
    {
        foreach (var a in Alerts)
            a.IsAcknowledged = true;
        UnacknowledgedCount = 0;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        Alerts.Clear();
        AlertCount = 0;
        UnacknowledgedCount = 0;
        MonitorStatus = IsMonitoring
            ? "Monitoring active — history cleared."
            : "History cleared.";
    }

    [RelayCommand]
    private void RefreshInstalledApps()
    {
        StatusMessage = "Loading installed applications...";
        IsBusy = true;
        IsProgressIndeterminate = true;

        try
        {
            var apps = AppAlertService.GetRegistryApps();
            Alerts.Clear();
            foreach (var app in apps.OrderBy(a => a.Name))
            {
                app.DetectedAt = DateTime.Now;
                app.IsAcknowledged = true;
                Alerts.Add(app);
            }
            AlertCount = Alerts.Count;
            UnacknowledgedCount = 0;
            MonitorStatus = $"Loaded {AlertCount} currently installed applications.";
            StatusMessage = "Done.";
        }
        catch (System.Security.SecurityException ex)
        {
            MonitorStatus = $"Failed to read registry: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            MonitorStatus = $"Access denied: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    private void OnNewAppDetected(AppInstallEntry entry)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Alerts.Insert(0, entry);
            AlertCount = Alerts.Count;
            UnacknowledgedCount = Alerts.Count(a => !a.IsAcknowledged);
            MonitorStatus = $"New app detected: {entry.Name}";
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _service.NewAppDetected -= OnNewAppDetected;
            _service.Dispose();
        }
        base.Dispose(disposing);
    }
}
