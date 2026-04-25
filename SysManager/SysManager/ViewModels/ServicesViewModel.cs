// SysManager · ServicesViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Services tab — lists all Windows services with gaming recommendations,
/// allows start/stop and startup type changes.
/// </summary>
public partial class ServicesViewModel : ViewModelBase
{
    private readonly PowerShellRunner _ps = new();
    private List<ServiceEntry> _allServices = new();

    public ObservableCollection<ServiceEntry> Services { get; } = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _selectedFilter = "All";
    [ObservableProperty] private string _sortBy = "Name";
    [ObservableProperty] private ServiceEntry? _selectedService;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _runningCount;

    public string[] FilterOptions { get; } =
        { "All", "Running", "Stopped", "Safe to disable", "Advanced" };

    public string[] SortOptions { get; } =
        { "Name", "Status", "Startup", "Recommendation" };

    public ServicesViewModel() => _ = RefreshAsync();

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(string value) => ApplyFilter();
    partial void OnSortByChanged(string value) => ApplyFilter();

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
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (Win32Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    private void ApplyFilterCore()
    {
        TotalCount = _allServices.Count;
        RunningCount = _allServices.Count(s => s.Status == "Running");
        ApplyFilter();
        StatusMessage = $"Loaded {TotalCount} services ({RunningCount} running).";
    }

    [RelayCommand]
    private void StartService(ServiceEntry? entry)
    {
        if (entry == null) return;
        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Starting services requires admin."; return; }

        var result = MessageBox.Show(
            $"Start service \"{entry.DisplayName}\"?",
            "Start Service — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            ServiceManagerService.StartService(entry.Name);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} started.";
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (System.ServiceProcess.TimeoutException) { StatusMessage = $"Timeout starting {entry.DisplayName}."; }
    }

    [RelayCommand]
    private void StopService(ServiceEntry? entry)
    {
        if (entry == null) return;
        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Stopping services requires admin."; return; }

        var result = MessageBox.Show(
            $"Stop service \"{entry.DisplayName}\"?\n\nThis may affect system functionality.",
            "Stop Service — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            ServiceManagerService.StopService(entry.Name);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} stopped.";
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (System.ServiceProcess.TimeoutException) { StatusMessage = $"Timeout stopping {entry.DisplayName}."; }
    }

    [RelayCommand]
    private async Task DisableServiceAsync(ServiceEntry? entry)
    {
        if (entry == null) return;
        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Changing startup type requires admin."; return; }

        var result = MessageBox.Show(
            $"Disable service \"{entry.DisplayName}\"?\n\nThis prevents the service from starting automatically.",
            "Disable Service — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            await ServiceManagerService.SetStartupTypeAsync(entry.Name, "disabled", _ps);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} set to Disabled.";
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task EnableServiceAsync(ServiceEntry? entry)
    {
        if (entry == null) return;
        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Changing startup type requires admin."; return; }

        try
        {
            await ServiceManagerService.SetStartupTypeAsync(entry.Name, "demand", _ps);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} set to Manual.";
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    private void ApplyFilter()
    {
        Services.Clear();
        var filtered = _allServices.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Filter))
            filtered = filtered.Where(s =>
                s.DisplayName.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(Filter, StringComparison.OrdinalIgnoreCase));

        filtered = SelectedFilter switch
        {
            "Running" => filtered.Where(s => s.Status == "Running"),
            "Stopped" => filtered.Where(s => s.Status == "Stopped"),
            "Safe to disable" => filtered.Where(s => s.Recommendation == "safe-to-disable"),
            "Advanced" => filtered.Where(s => s.Recommendation == "advanced"),
            _ => filtered
        };

        filtered = SortBy switch
        {
            "Status" => filtered.OrderBy(s => s.Status, StringComparer.OrdinalIgnoreCase),
            "Startup" => filtered.OrderBy(s => s.StartType, StringComparer.OrdinalIgnoreCase),
            "Recommendation" => filtered.OrderByDescending(s => s.Recommendation, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var s in filtered) Services.Add(s);
    }
}
