// SysManager · NotificationBlockerViewModel — mute app notification nags per app
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
/// Notification Blocker tab. Lists every app Windows has recorded as a notification
/// sender and lets the user mute the nagging ones (or everything at once) using the
/// same per-user switches Windows Settings writes. Switch flips update local state
/// only; the user must explicitly press "Apply" to write changes — mirroring the
/// pending-change flow of its sibling <see cref="PrivacyViewModel"/>.
/// </summary>
public sealed partial class NotificationBlockerViewModel : ViewModelBase
{
    private readonly INotificationBlockerService _service;
    private readonly Dictionary<NotificationApp, bool> _baselineStates = [];
    private bool _masterBaseline = true;

    public BulkObservableCollection<NotificationApp> Apps { get; } = new();
    public BulkObservableCollection<NotificationApp> FilteredApps { get; } = new();

    [ObservableProperty] private string _searchText = "";

    /// <summary>The user-wide master toggle (true = Windows may show any toasts at all).</summary>
    [ObservableProperty] private bool _masterEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private int _pendingChangeCount;

    public bool HasPendingChanges => PendingChangeCount > 0;

    public NotificationBlockerViewModel(INotificationBlockerService service)
    {
        _service = service;
        // Walk the notification-senders registry tree off the UI thread so first open
        // doesn't block; the UI update runs back on the UI thread (ConfigureAwait true).
        InitializeAsync(LoadAppsAsync);
    }

    private async Task LoadAppsAsync()
    {
        var (apps, master) = await Task.Run(() => (_service.GetApps(), _service.IsGlobalToastEnabled()))
            .ConfigureAwait(true);
        LoadApps(apps, master);
    }

    private void LoadApps(IReadOnlyList<NotificationApp> apps, bool masterEnabled)
    {
        foreach (var a in Apps)
            a.PropertyChanged -= OnAppPropertyChanged;

        Apps.ReplaceWith(apps);

        _baselineStates.Clear();
        foreach (var a in Apps)
        {
            _baselineStates[a] = a.IsEnabled;
            a.PropertyChanged += OnAppPropertyChanged;
        }

        // Baseline first: assigning MasterEnabled fires OnMasterEnabledChanged → RecomputePendingChanges,
        // which must compare against the NEW baseline, not last load's.
        _masterBaseline = masterEnabled;
        MasterEnabled = masterEnabled;

        ApplyFilter();
        RecomputePendingChanges();
        UpdateStatus();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnMasterEnabledChanged(bool value)
    {
        RecomputePendingChanges();
        UpdateStatus();
    }

    private void ApplyFilter()
    {
        IEnumerable<NotificationApp> source = Apps;

        if (!string.IsNullOrWhiteSpace(SearchText))
            source = source.Where(a =>
                a.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                a.Aumid.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        FilteredApps.ReplaceWith(source);
    }

    private void OnAppPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NotificationApp.IsEnabled)) return;
        RecomputePendingChanges();
        UpdateStatus();
    }

    private void RecomputePendingChanges()
    {
        var pending = MasterEnabled != _masterBaseline ? 1 : 0;
        foreach (var a in Apps)
            if (_baselineStates.TryGetValue(a, out var baseline) && baseline != a.IsEnabled)
                pending++;
        PendingChangeCount = pending;
    }

    [RelayCommand]
    private void ApplyChanges()
    {
        if (PendingChangeCount == 0)
        {
            StatusMessage = "No changes to apply.";
            return;
        }

        var changedApps = Apps
            .Where(a => _baselineStates.TryGetValue(a, out var baseline) && baseline != a.IsEnabled)
            .ToList();
        var masterChanged = MasterEnabled != _masterBaseline;

        var muteAllWarning = masterChanged && !MasterEnabled
            ? "\n\nTurning the master switch off silences ALL notifications — including calendar and reminder alerts — until you turn it back on."
            : "";

        if (!DialogService.Instance.Confirm(
                $"Apply {PendingChangeCount} notification change{(PendingChangeCount == 1 ? "" : "s")}?\n\n" +
                "Every change can be undone by switching it back and pressing Apply again." + muteAllWarning,
                "Confirm Notification Changes"))
        {
            StatusMessage = "Apply cancelled.";
            return;
        }

        // Only rebase the baseline for writes that actually succeeded — a failed write
        // stays "pending" so the user sees it wasn't applied rather than it silently vanishing.
        var applied = 0;
        var failed = 0;

        if (masterChanged)
        {
            if (_service.SetGlobalToastEnabled(MasterEnabled)) { _masterBaseline = MasterEnabled; applied++; }
            else failed++;
        }

        foreach (var a in changedApps)
        {
            if (_service.SetAppEnabled(a.Aumid, a.IsEnabled)) { _baselineStates[a] = a.IsEnabled; applied++; }
            else failed++;
        }

        RecomputePendingChanges();
        StatusMessage = failed == 0
            ? $"Applied {applied} change{(applied == 1 ? "" : "s")}."
            : $"Applied {applied} change{(applied == 1 ? "" : "s")}; {failed} failed — see the log for details.";
        Log.Information("Notification Blocker: applied {Applied} changes, {Failed} failed", applied, failed);
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        foreach (var a in Apps)
            if (_baselineStates.TryGetValue(a, out var baseline))
                a.IsEnabled = baseline;
        MasterEnabled = _masterBaseline;
        RecomputePendingChanges();
        StatusMessage = "Pending changes discarded.";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Re-read off the UI thread — mirrors the async initial load.
        var (apps, master) = await Task.Run(() => (_service.GetApps(), _service.IsGlobalToastEnabled()))
            .ConfigureAwait(true);
        LoadApps(apps, master);
        StatusMessage = "Notification senders refreshed.";
        Log.Information("Notification Blocker: refreshed sender list ({Count} apps)", Apps.Count);
    }

    private void UpdateStatus()
    {
        var muted = Apps.Count(a => !a.IsEnabled);
        var summary = MasterEnabled
            ? $"{Apps.Count} notification sender{(Apps.Count == 1 ? "" : "s")} found · {muted} muted."
            : $"All notifications are muted by the master switch · {Apps.Count} sender{(Apps.Count == 1 ? "" : "s")} found.";
        if (PendingChangeCount > 0)
            summary += $" {PendingChangeCount} pending change{(PendingChangeCount == 1 ? "" : "s")} — press Apply.";
        StatusMessage = summary;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var a in Apps)
                a.PropertyChanged -= OnAppPropertyChanged;
        }
        base.Dispose(disposing);
    }
}
