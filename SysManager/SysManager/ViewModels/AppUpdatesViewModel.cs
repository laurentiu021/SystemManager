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
    private readonly IWingetService _winget;
    private readonly EtaCalculator _upgradeEta = new();
    private CancellationTokenSource? _cts;
    private readonly Action<PowerShellLine> _lineHandler;

    /// <summary>
    /// Shown when winget.exe cannot be launched — App Installer isn't present or its
    /// execution alias is off (common on older/LTSC/Server machines). Plain-language so
    /// the non-technical persona knows the tab needs App Installer, not that something broke.
    /// </summary>
    internal const string WingetUnavailableMessage =
        "winget (App Installer) isn't available on this PC — install \"App Installer\" from the Microsoft Store to use this tab.";

    public BulkObservableCollection<AppPackage> Packages { get; } = new();
    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private bool _selectAll = true;
    [ObservableProperty] private string _upgradeEtaText = string.Empty;
    [ObservableProperty] private bool _isElevated;

    // Distinguishes the un-run state from a completed zero-result scan so the empty-state overlay
    // doesn't assert "No updates available — all packages are up to date" before the user has ever
    // scanned. Set true only after a scan completes (see ScanAsync).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private bool _hasScanned;

    public string EmptyTitle => HasScanned ? "No updates available" : "Not scanned yet";
    public string EmptyMessage => HasScanned
        ? "All detected packages are up to date."
        : "Run a check to scan for winget upgrades.";

    public AppUpdatesViewModel(IWingetService winget)
    {
        _winget = winget;
        _lineHandler = line => Console.Append(line);
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
        _winget.LineReceived += _lineHandler; // op-scoped subscription (see constructor note)
        try
        {
            var list = await _winget.ListUpgradableAsync(_cts.Token);
            Packages.ReplaceWith(list);
            HasScanned = true;
            StatusMessage = $"{Packages.Count} upgradable package(s) found";
        }
        catch (OperationCanceledException) { StatusMessage = "Scan cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        // winget.exe missing (App Installer not present / execution alias off) throws
        // Win32Exception "cannot find the file specified" from Process.Start. Without this
        // it escapes the AsyncRelayCommand to the global dispatcher handler and pops a raw
        // OS-error dialog on the tab's first action. Mirror UninstallerViewModel's handling.
        catch (System.ComponentModel.Win32Exception) { StatusMessage = WingetUnavailableMessage; }
        finally { _winget.LineReceived -= _lineHandler; IsBusy = false; IsProgressIndeterminate = false; }
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
        var wingetUnavailable = false;
        UpgradeEtaText = string.Empty;
        _upgradeEta.Reset();
        _winget.LineReceived += _lineHandler; // op-scoped subscription (see constructor note)
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
                // winget.exe missing throws Win32Exception. It won't reappear mid-batch, so
                // report it once and stop rather than failing every remaining row identically.
                catch (System.ComponentModel.Win32Exception)
                {
                    pkg.Status = "winget unavailable";
                    wingetUnavailable = true;
                    break;
                }
                attempted++;
            }
            Progress = 100;
            UpgradeEtaText = string.Empty;
            // Honest summary: separate succeeded from failed, and only mention failures
            // when there are any. If winget itself is missing, keep the friendly
            // "install App Installer" message instead of a misleading "Updated 0 of N".
            StatusMessage = wingetUnavailable
                ? WingetUnavailableMessage
                : failed == 0
                    ? $"Updated {succeeded} of {toUpgrade.Count}."
                    : $"Updated {succeeded} of {toUpgrade.Count} · {failed} failed.";
            Log.Information("App upgrade batch: {Succeeded} ok, {Failed} failed of {Total}",
                succeeded, failed, toUpgrade.Count);
        }
        finally { _winget.LineReceived -= _lineHandler; IsBusy = false; UpgradeEtaText = string.Empty; }
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
