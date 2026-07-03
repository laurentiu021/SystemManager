// SysManager · AppUpdatesViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class AppUpdatesViewModel : ViewModelBase
{
    private readonly WingetService _winget;
    private readonly EtaCalculator _upgradeEta = new();
    private CancellationTokenSource? _cts;
    private readonly Action<PowerShellLine> _lineHandler;

    public BulkObservableCollection<AppPackage> Packages { get; } = new();
    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private bool _selectAll = true;
    [ObservableProperty] private string _upgradeEtaText = string.Empty;
    [ObservableProperty] private bool _isElevated;

    public AppUpdatesViewModel(WingetService winget)
    {
        _winget = winget;
        _lineHandler = line => Console.Append(line);
        _winget.LineReceived += _lineHandler;
        // Re-evaluate the long-running commands' CanExecute when IsBusy flips. Scan and
        // UpgradeSelected both recreate the shared _cts; without this gate a second
        // command could dispose the CTS the first is still awaiting (ObjectDisposedException).
        PropertyChanged += OnVmPropertyChanged;
        IsElevated = SysManager.Helpers.AdminHelper.IsElevated();
    }

    /// <summary>
    /// Gate for the long-running commands. Scan and UpgradeSelected share <see cref="_cts"/>
    /// and each recreates it, so disabling both while one runs prevents a second command
    /// from disposing the CTS mid-flight. Cancel is intentionally NOT gated — it must stay
    /// enabled while an operation runs. Mirrors WindowsUpdateViewModel.
    /// </summary>
    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ScanCommand.NotifyCanExecuteChanged();
        UpgradeSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        Log.Information("Admin elevation requested from App Updates tab");
        if (SysManager.Helpers.AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    partial void OnSelectAllChanged(bool value)
    {
        foreach (var p in Packages) p.IsSelected = value;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Querying winget...";
        Packages.Clear();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var list = await _winget.ListUpgradableAsync(_cts.Token);
            Packages.ReplaceWith(list);
            StatusMessage = $"{Packages.Count} upgradable package(s) found";
        }
        catch (OperationCanceledException) { StatusMessage = "Scan cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task UpgradeSelectedAsync()
    {
        var toUpgrade = Packages.Where(p => p.IsSelected).ToList();
        if (toUpgrade.Count == 0) { StatusMessage = "No packages selected"; return; }

        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        int attempted = 0, succeeded = 0, failed = 0;
        UpgradeEtaText = string.Empty;
        _upgradeEta.Reset();
        try
        {
            foreach (var pkg in toUpgrade)
            {
                if (_cts.IsCancellationRequested) break;
                pkg.Status = "Upgrading…";
                StatusMessage = $"Upgrading {pkg.Name} ({attempted + 1}/{toUpgrade.Count})";
                Progress = (int)((attempted / (double)toUpgrade.Count) * 100);
                UpgradeEtaText = _upgradeEta.Update(Progress);
                try
                {
                    // WingetResult carries a friendly message translated from the exit code,
                    // so the per-app status is human-readable (never a raw "exit 0x8A15…").
                    var result = await _winget.UpgradeAsync(pkg.Id, _cts.Token);
                    pkg.Status = result.FriendlyMessage;
                    if (result.Succeeded) succeeded++; else failed++;
                }
                catch (OperationCanceledException) { pkg.Status = "Cancelled"; break; }
                catch (InvalidOperationException ex) { pkg.Status = $"Error: {ex.Message}"; failed++; }
                // A single invalid package Id throws ArgumentException from UpgradeAsync
                // BEFORE any process runs; record it on the row and keep upgrading the rest
                // rather than aborting the whole batch.
                catch (ArgumentException ex) { pkg.Status = $"Error: {ex.Message}"; failed++; }
                attempted++;
            }
            Progress = 100;
            UpgradeEtaText = string.Empty;
            // Honest summary: separate succeeded from failed, and only mention failures
            // when there are any.
            StatusMessage = failed == 0
                ? $"Updated {succeeded} of {toUpgrade.Count}."
                : $"Updated {succeeded} of {toUpgrade.Count} · {failed} failed.";
            Log.Information("App upgrade batch: {Succeeded} ok, {Failed} failed of {Total}",
                succeeded, failed, toUpgrade.Count);
        }
        finally { IsBusy = false; UpgradeEtaText = string.Empty; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _winget.LineReceived -= _lineHandler;
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
