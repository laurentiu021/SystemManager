// SysManager · ServicesViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Services tab — lists all Windows services with gaming recommendations,
/// allows start/stop and startup type changes.
/// </summary>
public sealed partial class ServicesViewModel : ViewModelBase
{
    private readonly PowerShellRunner _ps;
    private List<ServiceEntry> _allServices = [];

    public BulkObservableCollection<ServiceEntry> Services { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _selectedFilter = "All";
    [ObservableProperty] private ServiceEntry? _selectedService;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _safeCount;
    [ObservableProperty] private int _cautionCount;
    [ObservableProperty] private int _criticalCount;

    public string[] FilterOptions { get; } =
        { "All", "Running", "Stopped", "Safe", "Caution", "Critical" };

    public ServicesViewModel(PowerShellRunner ps)
    {
        _ps = ps;
        IsElevated = AdminHelper.IsElevated();
        InitializeAsync(InitAsync);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    private async Task InitAsync()
    {
        try { await RefreshAsync(); }
        catch (InvalidOperationException ex) { Log.Warning("Services auto-refresh failed: {Error}", ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warning("Services auto-refresh failed: {Error}", ex.Message); }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Loading services…";
        try
        {
            _allServices = await Task.Run(ServiceManagerService.GetAllServices);
            // Ensure collection updates happen on the UI thread to prevent
            // cross-thread exceptions when navigating during concurrent scans (#154).
            if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
                d.Invoke(ApplyFilterCore);
            else
                ApplyFilterCore();
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Service scan failed: {ex.Message}"; }
        catch (Win32Exception ex) { StatusMessage = $"Service scan failed: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    private void ApplyFilterCore()
    {
        TotalCount = _allServices.Count;
        RunningCount = _allServices.Count(s => s.Status == "Running");
        ApplyFilter();
        StatusMessage = $"Loaded {TotalCount} services ({RunningCount} running).";
        ToastService.Instance.Show("Services refreshed", $"{TotalCount} services ({RunningCount} running)");
    }

    [RelayCommand]
    private async Task StartServiceAsync(ServiceEntry? entry)
    {
        if (entry is null) return;
        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Starting services requires admin."; return; }

        if (!DialogService.Instance.Confirm(
            $"Start service \"{entry.DisplayName}\"?",
            "Start Service — Confirm")) return;

        try
        {
            // Resume on the UI thread (no ConfigureAwait(false)): the continuation updates
            // bound state (RefreshStatus + StatusMessage), matching Enable/DisableServiceAsync.
            await ServiceManagerService.StartServiceAsync(entry.Name);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} started.";
            Log.Information("Service started: {ServiceName}", entry.Name);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Start service failed: {ex.Message}"; }
        catch (System.ServiceProcess.TimeoutException) { StatusMessage = $"Timeout starting {entry.DisplayName}."; }
    }

    [RelayCommand]
    private async Task StopServiceAsync(ServiceEntry? entry)
    {
        if (entry is null) return;

        // Stopping a boot/logon-critical service (RpcSs, DcomLaunch, ProfSvc, lsass, …)
        // can freeze the session or force a reboot just as surely as disabling it, so it
        // gets the same unconditional refusal as DisableServiceAsync rather than the
        // neutral "may affect system functionality" confirm. Checked before the elevation
        // guard — it can never proceed regardless of admin.
        if (entry.SafetyLevel == SafetyLevel.Critical)
        {
            StatusMessage = $"⛔ \"{entry.DisplayName}\" is critical and cannot be stopped — {entry.SafetyDescription}";
            Log.Warning("Refused to stop critical service: {ServiceName} ({DisplayName})", entry.Name, entry.DisplayName);
            return;
        }

        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Stopping services requires admin."; return; }

        if (!DialogService.Instance.Confirm(
            $"Stop service \"{entry.DisplayName}\"?\n\nThis may affect system functionality.",
            "Stop Service — Confirm")) return;

        try
        {
            // Resume on the UI thread — see StartServiceAsync.
            await ServiceManagerService.StopServiceAsync(entry.Name);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} stopped.";
            Log.Information("Service stopped: {ServiceName}", entry.Name);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Stop service failed: {ex.Message}"; }
        catch (System.ServiceProcess.TimeoutException) { StatusMessage = $"Timeout stopping {entry.DisplayName}."; }
    }

    [RelayCommand]
    private async Task DisableServiceAsync(ServiceEntry? entry)
    {
        if (entry is null) return;

        // A boot/logon-critical service must never be disabled: setting RpcSs,
        // DcomLaunch, ProfSvc, lsass, etc. to Disabled can prevent Windows from
        // booting or logging in. Refuse outright rather than hide the risk behind
        // the same neutral confirm shown for safe-to-disable services. Checked
        // before the elevation guard — it can never proceed regardless of admin.
        if (entry.SafetyLevel == SafetyLevel.Critical)
        {
            StatusMessage = $"⛔ \"{entry.DisplayName}\" is critical and cannot be disabled — {entry.SafetyDescription}";
            Log.Warning("Refused to disable critical service: {ServiceName} ({DisplayName})", entry.Name, entry.DisplayName);
            return;
        }

        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Changing startup type requires admin."; return; }

        if (!DialogService.Instance.Confirm(
            $"Disable service \"{entry.DisplayName}\"?\n\nThis prevents the service from starting automatically.",
            "Disable Service — Confirm")) return;

        // Snapshot the current startup type BEFORE disabling so Enable can restore the
        // exact previous type (e.g. Automatic) instead of always falling back to Manual.
        var previous = entry.StartType;

        try
        {
            await ServiceManagerService.SetStartupTypeAsync(entry.Name, "disabled", _ps);
            entry.PreviousStartType = previous;
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} set to Disabled.";
            Log.Information("Service disabled: {ServiceName} (was {Previous})", entry.Name, previous);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Disable service failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task EnableServiceAsync(ServiceEntry? entry)
    {
        if (entry is null) return;
        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Changing startup type requires admin."; return; }

        // Restore the startup type the service had before SysManager disabled it. If we
        // never disabled it this session, fall back to Manual (the conservative default).
        var targetToken = ServiceManagerService.StartTypeToScToken(entry.PreviousStartType);

        try
        {
            await ServiceManagerService.SetStartupTypeAsync(entry.Name, targetToken, _ps);
            entry.PreviousStartType = null;
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} set to {entry.StartType}.";
            Log.Information("Service enabled: {ServiceName} -> {StartType}", entry.Name, entry.StartType);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Enable service failed: {ex.Message}"; }
    }

    private void ApplyFilter()
    {
        var filtered = _allServices.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(FilterText))
            filtered = filtered.Where(s =>
                s.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        filtered = SelectedFilter switch
        {
            "Running" => filtered.Where(s => s.Status == "Running"),
            "Stopped" => filtered.Where(s => s.Status == "Stopped"),
            "Safe" => filtered.Where(s => s.SafetyLevel == SafetyLevel.Safe),
            "Caution" => filtered.Where(s => s.SafetyLevel == SafetyLevel.Caution),
            "Critical" => filtered.Where(s => s.SafetyLevel == SafetyLevel.Critical),
            _ => filtered
        };

        Services.ReplaceWith(filtered.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase));

        int safe = 0, caution = 0, critical = 0;
        foreach (var s in _allServices)
        {
            switch (s.SafetyLevel)
            {
                case SafetyLevel.Safe: safe++; break;
                case SafetyLevel.Caution: caution++; break;
                case SafetyLevel.Critical: critical++; break;
            }
        }
        SafeCount = safe;
        CautionCount = caution;
        CriticalCount = critical;
    }

    [RelayCommand]
    private void ToggleHighlight(object? parameter)
    {
        if (parameter is ServiceEntry entry)
            entry.IsHighlighted = !entry.IsHighlighted;
    }
}
