// SysManager · UninstallerViewModel — uninstall apps via winget
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Uninstaller tab — lists installed apps, filter, select, uninstall.
/// </summary>
public sealed partial class UninstallerViewModel : ViewModelBase
{
    private readonly UninstallerService _service;
    private readonly EtaCalculator _uninstallEta = new();
    private readonly Action<PowerShellLine> _lineHandler;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<InstalledApp> AllApps { get; } = new();
    public BulkObservableCollection<InstalledApp> FilteredApps { get; } = new();
    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private string _summary = "Click Scan to list installed applications.";
    [ObservableProperty] private string _uninstallEtaText = string.Empty;
    [ObservableProperty] private bool _isElevated;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public UninstallerViewModel(UninstallerService service)
    {
        _service = service;
        _lineHandler = line => Console.Append(line);
        _service.LineReceived += _lineHandler;
        // Scan and UninstallSelected both recreate the shared _cts; without this gate a
        // second command could dispose the CTS the first is still awaiting
        // (ObjectDisposedException). Re-evaluate both commands' CanExecute when IsBusy flips.
        PropertyChanged += OnVmPropertyChanged;
        IsElevated = AdminHelper.IsElevated();
    }

    /// <summary>
    /// Gate for the long-running commands. Scan and UninstallSelected share <see cref="_cts"/>
    /// and each recreates it, so disabling both while one runs prevents a second command from
    /// disposing the CTS mid-flight. Cancel is intentionally NOT gated. Mirrors the App Updates
    /// and Windows Update tabs.
    /// </summary>
    private bool NotBusy => !IsBusy;

    /// <summary>
    /// True once a scan has listed at least one app. Select-all / deselect / uninstall
    /// act on the list, so they stay disabled on an empty (unscanned) list rather than
    /// appearing operable with nothing to act on.
    /// </summary>
    private bool HasApps => AppCount > 0;

    /// <summary>
    /// Uninstall needs a populated list, no active command, and an unelevated
    /// SysManager process. The selected package owns any UAC request it needs.
    /// </summary>
    private bool CanUninstall => NotBusy && HasApps && !IsElevated;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ScanCommand.NotifyCanExecuteChanged();
        UninstallSelectedCommand.NotifyCanExecuteChanged();
    }

    // AppCount is refreshed by ApplyFilter; re-evaluate the list-dependent commands
    // whenever it changes so their enabled state tracks the (un)populated list.
    partial void OnAppCountChanged(int value)
    {
        UninstallSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        DeselectAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsElevatedChanged(bool value) =>
        UninstallSelectedCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Querying winget list…";
        FilteredApps.Clear();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var list = await _service.ListInstalledAsync(_cts.Token);
            foreach (var app in list)
                app.Icon ??= IconExtractorService.FallbackIcon;
            AllApps.ReplaceWith(list);

            ApplyFilter();
            StatusMessage = $"Found {AllApps.Count} installed applications.";
            ToastService.Instance.Show("Uninstaller scan complete", $"Found {AllApps.Count} installed applications");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        // winget.exe missing (App Installer absent / execution alias off) throws
        // Win32Exception from Process.Start. Scan is the tab's first action, so without
        // this the raw OS-error dialog pops immediately. Reuse the AppUpdates message so
        // both winget tabs speak with one voice.
        catch (System.ComponentModel.Win32Exception)
        {
            StatusMessage = AppUpdatesViewModel.WingetUnavailableMessage;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
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

        if (!DialogService.Instance.Confirm(
            $"You are about to uninstall {toRemove.Count} application(s):\n\n{names}\n\nThis cannot be undone. Continue?",
            "Confirm uninstall")) return;

        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        int completed = 0;
        int removed = 0;
        int failed = 0;
        int restartRequired = 0;
        bool cancellationRequested = false;
        UninstallEtaText = string.Empty;
        _uninstallEta.Reset();

        try
        {
            foreach (var app in toRemove)
            {
                if (_cts.IsCancellationRequested)
                {
                    cancellationRequested = true;
                    break;
                }

                app.Status = "Uninstalling...";
                StatusMessage = $"Uninstalling {app.Name} ({completed + 1}/{toRemove.Count})...";
                Progress = (int)(completed * 100.0 / toRemove.Count);
                UninstallEtaText = _uninstallEta.Update(Progress);
                var currentCompleted = false;

                try
                {
                    var code = (string.IsNullOrWhiteSpace(app.Source)
                        && !string.IsNullOrWhiteSpace(app.UninstallString))
                        ? await _service.UninstallLocalAsync(app, _cts.Token)
                        : await _service.UninstallAsync(app.Id, _cts.Token);

                    currentCompleted = true;
                    if (IsSuccessfulUninstallExitCode(code))
                    {
                        var needsRestart = RequiresRestartAfterUninstall(code);
                        app.Status = needsRestart ? "Removed - restart required" : "Removed";
                        removed++;
                        if (needsRestart)
                            restartRequired++;
                        AllApps.Remove(app);
                        FilteredApps.Remove(app);
                    }
                    else
                    {
                        app.Status = DescribeUninstallFailure(code, app.Name);
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    app.Status = "Cancelled";
                    cancellationRequested = true;
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    currentCompleted = !_cts.IsCancellationRequested;
                }
                // A failed uninstaller launch (missing/blocked exe) must not abort the
                // whole batch - record it on the row and continue with the next app.
                catch (System.ComponentModel.Win32Exception ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    currentCompleted = true;
                }
                catch (System.IO.IOException ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    currentCompleted = true;
                }
                // An unparseable package Id (e.g. an ARP GUID) throws ArgumentException from
                // UninstallAsync before any process runs; record it and continue the batch.
                catch (ArgumentException ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    currentCompleted = true;
                }

                if (currentCompleted)
                    completed++;

                Progress = (int)(completed * 100.0 / toRemove.Count);
                UninstallEtaText = _uninstallEta.Update(Progress);

                if (_cts.IsCancellationRequested && completed < toRemove.Count)
                {
                    cancellationRequested = true;
                    break;
                }
            }

            UninstallEtaText = string.Empty;
            var restartMessage = restartRequired switch
            {
                0 => string.Empty,
                1 => " Restart required for 1 app.",
                _ => $" Restart required for {restartRequired} apps."
            };
            if (cancellationRequested)
            {
                StatusMessage = $"Uninstall cancelled after {completed}/{toRemove.Count} completed. Removed {removed}; failed {failed}.{restartMessage}";

                Log.Information(
                    "Uninstall batch cancelled: {Completed}/{Total} completed, {Removed} removed, {Failed} failed, {RestartRequired} need restart",
                    completed,
                    toRemove.Count,
                    removed,
                    failed,
                    restartRequired);
            }
            else if (failed > 0)
            {
                Progress = 100;
                StatusMessage = $"Uninstall finished with errors. Removed {removed}; failed {failed}.{restartMessage}";

                Log.Warning(
                    "Uninstall batch finished with errors: {Removed} removed, {Failed} failed, {RestartRequired} need restart, {Total} total",
                    removed,
                    failed,
                    restartRequired,
                    toRemove.Count);
            }
            else
            {
                Progress = 100;
                StatusMessage = $"Completed {removed}/{toRemove.Count} uninstalls.{restartMessage}";
                ToastService.Instance.Show(
                    "Uninstall complete",
                    $"Completed {removed}/{toRemove.Count} uninstalls.{restartMessage}");
                Log.Information(
                    "Uninstall batch completed: {Removed}/{Total}, {RestartRequired} need restart",
                    removed,
                    toRemove.Count,
                    restartRequired);
            }
        }
        finally
        {
            IsBusy = false;
            UninstallEtaText = string.Empty;
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _service.LineReceived -= _lineHandler;
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    [RelayCommand(CanExecute = nameof(HasApps))]
    private void SelectAll()
    {
        if (FilteredApps.Count > 20
            && string.IsNullOrWhiteSpace(FilterText)
            && !DialogService.Instance.Confirm(
                $"This will select all {FilteredApps.Count} applications.\n\nUse the filter to narrow down the list first.\nAre you sure you want to select all?",
                "Select all apps"))
        {
            return;
        }

        foreach (var app in FilteredApps) app.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(HasApps))]
    private void DeselectAll()
    {
        foreach (var app in FilteredApps) app.IsSelected = false;
    }

    private void ApplyFilter()
    {
        IEnumerable<InstalledApp> source = AllApps;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            source = source.Where(a =>
                a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                a.Id.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        source = source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        FilteredApps.ReplaceWith(source);

        AppCount = FilteredApps.Count;
        Summary = $"{AppCount} apps{(AllApps.Count != AppCount ? $" (of {AllApps.Count} total)" : "")}";
    }

    // Windows Installer uses 1641 and 3010 for successful removal that requires a restart.
    private static bool IsSuccessfulUninstallExitCode(int exitCode) =>
        exitCode is 0 or 1641 or 3010;

    private static bool RequiresRestartAfterUninstall(int exitCode) =>
        exitCode is 1641 or 3010;

    /// <summary>
    /// Translates a winget uninstall exit code into a human-readable message so the user knows why
    /// the uninstall failed and what to try next.
    /// <para>The mapping itself moved to <see cref="WingetFailure.DescribeUninstallFailure"/>, next to
    /// the install-side one, so the three winget tabs cannot drift apart again — Bulk Installer had
    /// been writing raw exit codes while this tab explained the same numbers. This overload stays as
    /// the call site (and its tests) already read it; <paramref name="appName"/> is not part of the
    /// sentence, which is why it is unused here.</para>
    /// </summary>
    internal static string DescribeUninstallFailure(int exitCode, string appName) =>
        WingetFailure.DescribeUninstallFailure(exitCode);
}
