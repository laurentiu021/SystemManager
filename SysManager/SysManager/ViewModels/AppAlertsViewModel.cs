// SysManager · AppAlertsViewModel — monitors and alerts on new app installations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// App Alerts tab — monitors for new application installations and shows
/// a timestamped history of detected installs.
/// </summary>
/// <remarks>
/// The history is kept in <see cref="IAppAlertHistory"/>, so it outlives the session and Recent Changes can say when a
/// program appeared (#1507). It is read and written where the list on screen changes, on the UI thread, as the activity
/// log is: a file of at most 200 short entries, written on a detection or a click, and in the order the list changed.
/// </remarks>
public sealed partial class AppAlertsViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanForNewAppsCommand;

    private readonly AppAlertService _service;
    private readonly IAppAlertHistory _history;
    private readonly Dispatcher _dispatcher;

    public BulkObservableCollection<AppInstallEntry> Alerts { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanForNewAppsCommand))]
    private bool _isMonitoring;
    [ObservableProperty] private string _monitorStatus = "Click Start to begin monitoring for new installations.";
    [ObservableProperty] private int _alertCount;
    [ObservableProperty] private int _unacknowledgedCount;

    public AppAlertsViewModel(AppAlertService service, IAppAlertHistory history)
    {
        _service = service;
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _service.NewAppDetected += OnNewAppDetected;
        InitializeAsync(() => { LoadHistory(); return Task.CompletedTask; });
    }

    /// <summary>Lists what earlier sessions noticed.</summary>
    private void LoadHistory()
    {
        var saved = _history.Load();
        if (!saved.Readable)
        {
            MonitorStatus = "The installations noticed earlier could not be read. New ones still show up here while SysManager is open.";
            return;
        }
        if (saved.Alerts.Count == 0) return;

        Alerts.ReplaceWith(saved.Alerts);
        AlertCount = Alerts.Count;
        UnacknowledgedCount = Alerts.Count(a => !a.IsAcknowledged);
        MonitorStatus = saved.Alerts.Count == 1
            ? "1 installation noticed earlier is listed below. Click Start to watch for new ones."
            : $"{saved.Alerts.Count} installations noticed earlier are listed below. Click Start to watch for new ones.";
    }

    [RelayCommand]
    private async Task StartMonitoringAsync()
    {
        if (IsMonitoring) return;

        // TakeBaseline() walks Program Files / LocalAppData\Programs AND enumerates both
        // HKLM Uninstall trees (hundreds of subkeys) — the same enumeration
        // ScanForNewAppsAsync offloads. Running it synchronously in the
        // command froze the UI for the whole scan. Offload it (and Start(), whose
        // FileSystemWatcher creation is thread-agnostic and whose NewAppDetected event is
        // marshaled via the SynchronizationContext captured at service construction), then
        // resume on the UI thread to set the bound state.
        MonitorStatus = "Starting monitoring…";
        await Task.Run(() =>
        {
            _service.TakeBaseline();
            _service.Start();
        }).ConfigureAwait(true);

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
        _history.AcknowledgeAll();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        // This list is the only record of what installed itself, and Recent Changes reads it too. Confirm before
        // discarding it — but stay frictionless when there is nothing to lose, otherwise the prompt is pure noise.
        if (AlertCount > 0 && !DialogService.Instance.Confirm(
                $"Clear all {AlertCount} recorded alert{(AlertCount == 1 ? "" : "s")}?\n\n" +
                "They are also taken off Recent Changes, and this cannot be undone.",
                "Clear History — Confirm"))
            return;

        var cleared = _history.Clear();
        Alerts.Clear();
        AlertCount = 0;
        UnacknowledgedCount = 0;
        MonitorStatus = !cleared
            ? "Cleared from this list, but the saved copy could not be removed, so it is back the next time SysManager starts."
            : IsMonitoring
                ? "Monitoring active — history cleared."
                : "History cleared.";
    }

    /// <summary>
    /// Checks for new installations now rather than at the next 30-second pass. What F5 does on this tab.
    /// </summary>
    /// <remarks>
    /// F5 used to run "Show Installed", which replaced the detected installs with every program on the PC,
    /// stamped each with the time of the keypress as its detection time, and asked nothing — while Clear
    /// History, the deliberate way to lose the same list, asks first because it is the only record. A check
    /// can only add to the list. To see everything that is installed, the Uninstaller lists it.
    /// <para>Runs only while monitoring: the check compares against the list taken when monitoring started.
    /// A detection it finds arrives through <see cref="OnNewAppDetected"/> like any other, which rewrites the
    /// status line after this one.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsMonitoring))]
    private async Task ScanForNewAppsAsync()
    {
        // The uninstall-tree enumeration walks hundreds of HKLM subkeys — off the UI thread, like the baseline.
        var found = await Task.Run(_service.CheckNow).ConfigureAwait(true);
        if (found == 0)
            MonitorStatus = "Checked just now — no new installations. Monitoring active.";
    }

    private void OnNewAppDetected(AppInstallEntry entry) => _dispatcher.BeginInvoke(() => Record(entry));

    /// <summary>Lists one detection, keeps it, and says so outside this tab. Runs on the UI thread.</summary>
    internal void Record(AppInstallEntry entry)
    {
        _history.Add(entry);
        Alerts.Insert(0, entry);
        AlertCount = Alerts.Count;
        UnacknowledgedCount = Alerts.Count(a => !a.IsAcknowledged);
        MonitorStatus = $"New app detected: {entry.Name}";

        // Surface it outside this tab. The whole point of monitoring is to learn about an
        // install you did not start, and the user is almost never sitting on this tab when
        // that happens — the list entry and the status line were only visible to someone
        // already looking at them, so a detection went unnoticed.
        ToastService.Instance.Show("New app installed", entry.Name);
    }

    /// <summary>Whether there are alerts worth exporting.</summary>
    public bool HasAlerts => AlertCount > 0;

    /// <summary>
    /// Writes the new-app alerts to a CSV the user picks a location for.
    /// </summary>
    /// <remarks>
    /// The list exists to answer "what appeared on this PC without me installing it", which is exactly the question someone asks a more technical friend — so it needs to leave the screen.
    /// <para>The file goes only where the dialog is pointed — nothing is written to a default location and
    /// nothing leaves the machine.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasAlerts))]
    private async Task ExportCsvAsync()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"SysManager-AppAlerts-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}.csv",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var csv = AppAlertService.ToCsv(Alerts);
            await File.WriteAllTextAsync(dlg.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusMessage = $"Exported {Alerts.Count} alert(s) to {Path.GetFileName(dlg.FileName)}.";
            ToastService.Instance.Show("App alerts exported", Path.GetFileName(dlg.FileName));
        }
        catch (IOException ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Export failed (access denied): {ex.Message}"; }
    }

    partial void OnAlertCountChanged(int value) => ExportCsvCommand.NotifyCanExecuteChanged();

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
