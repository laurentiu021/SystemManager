// SysManager · UninstallerViewModel — uninstall apps via winget
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Uninstaller tab — lists installed apps, filter, select, uninstall.
/// </summary>
public partial class UninstallerViewModel : ViewModelBase
{
    private readonly UninstallerService _service;
    private CancellationTokenSource? _cts;

    public ObservableCollection<InstalledApp> AllApps { get; } = new();
    public ObservableCollection<InstalledApp> FilteredApps { get; } = new();
    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private string _summary = "Click Scan to list installed applications.";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public UninstallerViewModel(PowerShellRunner runner)
    {
        _service = new UninstallerService(runner);
        _service.LineReceived += line => Console.Append(line);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Querying winget list…";
        AllApps.Clear();
        FilteredApps.Clear();
        _cts = new CancellationTokenSource();

        try
        {
            var list = await _service.ListInstalledAsync(_cts.Token);
            foreach (var app in list)
                AllApps.Add(app);

            ApplyFilter();
            StatusMessage = $"Found {AllApps.Count} installed applications.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        var toRemove = FilteredApps.Where(a => a.IsSelected).ToList();
        if (toRemove.Count == 0)
        {
            StatusMessage = "No apps selected.";
            return;
        }

        var names = string.Join("\n", toRemove.Take(10).Select(a => $"  • {a.Name}"));
        if (toRemove.Count > 10)
            names += $"\n  … and {toRemove.Count - 10} more";

        var result = System.Windows.MessageBox.Show(
            $"You are about to uninstall {toRemove.Count} application(s):\n\n{names}\n\nThis cannot be undone. Continue?",
            "Confirm uninstall",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        int done = 0;

        try
        {
            foreach (var app in toRemove)
            {
                if (_cts.IsCancellationRequested) break;
                app.Status = "Uninstalling…";
                StatusMessage = $"Uninstalling {app.Name} ({done + 1}/{toRemove.Count})…";
                Progress = (int)(done * 100.0 / toRemove.Count);

                try
                {
                    var code = await _service.UninstallAsync(app.Id, _cts.Token);
                    app.Status = code == 0 ? "Removed" : $"Failed (exit {code})";
                    if (code == 0)
                    {
                        AllApps.Remove(app);
                        FilteredApps.Remove(app);
                    }
                }
                catch (OperationCanceledException) { app.Status = "Cancelled"; break; }
                catch (InvalidOperationException ex) { app.Status = $"Error: {ex.Message}"; }
                done++;
            }

            Progress = 100;
            StatusMessage = $"Completed {done}/{toRemove.Count} uninstalls.";
        }
        finally
        {
            IsBusy = false;
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        if (FilteredApps.Count > 20 && string.IsNullOrWhiteSpace(FilterText))
        {
            var result = System.Windows.MessageBox.Show(
                $"This will select all {FilteredApps.Count} applications.\n\nUse the filter to narrow down the list first.\nAre you sure you want to select all?",
                "Select all apps",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.Yes) return;
        }

        foreach (var app in FilteredApps) app.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var app in FilteredApps) app.IsSelected = false;
    }

    private void ApplyFilter()
    {
        FilteredApps.Clear();
        IEnumerable<InstalledApp> source = AllApps;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            source = source.Where(a =>
                a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                a.Id.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var app in source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            FilteredApps.Add(app);

        AppCount = FilteredApps.Count;
        Summary = $"{AppCount} apps{(AllApps.Count != AppCount ? $" (of {AllApps.Count} total)" : "")}";
    }
}
