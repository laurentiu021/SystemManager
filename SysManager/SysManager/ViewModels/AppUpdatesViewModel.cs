// SysManager · AppUpdatesViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class AppUpdatesViewModel : ViewModelBase
{
    private readonly WingetService _winget;
    private CancellationTokenSource? _cts;

    public ObservableCollection<AppPackage> Packages { get; } = new();
    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private bool _selectAll = true;
    [ObservableProperty] private bool _isElevated;

    public AppUpdatesViewModel(WingetService winget)
    {
        _winget = winget;
        _winget.LineReceived += line => Console.Append(line);
        IsElevated = SysManager.Helpers.AdminHelper.IsElevated();
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        Log.Information("Admin elevation requested from App Updates tab");
        if (SysManager.Helpers.AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current.Shutdown();
    }

    partial void OnSelectAllChanged(bool value)
    {
        foreach (var p in Packages) p.IsSelected = value;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Querying winget...";
        Packages.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            var list = await _winget.ListUpgradableAsync(_cts.Token);
            foreach (var p in list) Packages.Add(p);
            StatusMessage = $"{Packages.Count} upgradable package(s) found";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task UpgradeSelectedAsync()
    {
        var toUpgrade = Packages.Where(p => p.IsSelected).ToList();
        if (toUpgrade.Count == 0) { StatusMessage = "No packages selected"; return; }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        int done = 0;
        try
        {
            foreach (var pkg in toUpgrade)
            {
                if (_cts.IsCancellationRequested) break;
                pkg.Status = "Upgrading...";
                StatusMessage = $"Upgrading {pkg.Name} ({done + 1}/{toUpgrade.Count})";
                Progress = (int)((done / (double)toUpgrade.Count) * 100);
                try
                {
                    var code = await _winget.UpgradeAsync(pkg.Id, _cts.Token);
                    pkg.Status = code == 0 ? "Done" : $"Failed (exit {code})";
                }
                catch (Exception ex) { pkg.Status = $"Error: {ex.Message}"; }
                done++;
            }
            Progress = 100;
            StatusMessage = $"Completed {done}/{toUpgrade.Count}";
            Log.Information("App upgrade batch completed: {Done}/{Total}", done, toUpgrade.Count);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
