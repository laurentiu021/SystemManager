// SysManager · PrivacyViewModel — privacy toggles management
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
/// ViewModel for the Privacy Toggles tab. Loads registry-backed toggles
/// and groups them by category. Toggle flips update local state only;
/// the user must explicitly press "Apply" to write changes to the registry.
/// Apply takes the shared session restore point first, so the protection no longer depends on
/// whether the user reached these toggles here or through Tweaks Hub.
/// </summary>
public sealed partial class PrivacyViewModel : ViewModelBase
{
    private readonly PrivacyService _service;
    private readonly ISessionRestorePoint _restorePoint;
    private readonly Dictionary<PrivacyToggle, bool> _baselineStates = [];

    public BulkObservableCollection<PrivacyToggle> Toggles { get; } = new();

    [ObservableProperty] private List<string> _categories = [];
    [ObservableProperty] private string _selectedCategory = "All";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private int _pendingChangeCount;

    public bool HasPendingChanges => PendingChangeCount > 0;

    public BulkObservableCollection<PrivacyToggle> FilteredToggles { get; } = new();

    public PrivacyViewModel(PrivacyService service, ISessionRestorePoint restorePoint)
    {
        _service = service;
        _restorePoint = restorePoint;
        IsElevated = AdminHelper.IsElevated();
        // Read the registry-backed toggles off the UI thread so the eagerly-built VM
        // doesn't block startup; the UI update runs back on the UI thread (ConfigureAwait true).
        InitializeAsync(LoadTogglesAsync);
    }

    private async Task LoadTogglesAsync()
    {
        // The view binds a progress bar to IsBusy and the sidebar spinner reads the same flag, but
        // this VM never set it — so reading every privacy registry key produced no feedback at all.
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var loaded = await Task.Run(_service.LoadToggles).ConfigureAwait(true);
            LoadToggles(loaded);
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    private void LoadToggles(List<PrivacyToggle> loaded)
    {
        // Unsubscribe from old toggles
        foreach (var t in Toggles)
            t.PropertyChanged -= OnTogglePropertyChanged;

        Toggles.ReplaceWith(loaded);

        // Capture baseline so we can compute the pending-change count.
        _baselineStates.Clear();
        foreach (var t in Toggles)
        {
            _baselineStates[t] = t.IsEnabled;
            t.PropertyChanged += OnTogglePropertyChanged;
        }

        // Build category list
        List<string> cats = ["All"];
        cats.AddRange(Toggles.Select(t => t.Category).Distinct().OrderBy(c => c));
        Categories = cats;
        SelectedCategory = "All";

        ApplyFilter();
        RecomputePendingChanges();
        UpdateStatus();
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<PrivacyToggle> source = Toggles;

        if (!string.IsNullOrEmpty(SelectedCategory) && SelectedCategory != "All")
            source = source.Where(t => t.Category == SelectedCategory);

        FilteredToggles.ReplaceWith(source);
    }

    private void OnTogglePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PrivacyToggle.IsEnabled)) return;
        RecomputePendingChanges();
        UpdateStatus();
    }

    private void RecomputePendingChanges()
    {
        var pending = 0;
        foreach (var t in Toggles)
            if (_baselineStates.TryGetValue(t, out var baseline) && baseline != t.IsEnabled)
                pending++;
        PendingChangeCount = pending;
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private async Task ApplyChanges()
    {
        if (PendingChangeCount == 0)
        {
            StatusMessage = "No changes to apply.";
            return;
        }

        var changed = Toggles
            .Where(t => _baselineStates.TryGetValue(t, out var baseline) && baseline != t.IsEnabled)
            .ToList();

        if (!DialogService.Instance.Confirm(
                $"Apply {changed.Count} privacy change{(changed.Count == 1 ? "" : "s")} to the Windows registry?\n\n" +
                "Each toggle can be reverted by switching it back and pressing Apply again.",
                "Confirm Privacy Changes"))
        {
            StatusMessage = "Apply cancelled.";
            return;
        }

        // Before the write, never after: a snapshot taken afterwards would record the state the
        // user is trying to be able to get back FROM. Taken after the confirmation, so declining
        // costs nothing, and it is the same seam Tweaks Hub uses rather than a second copy.
        var snapshotTaken = await _restorePoint
            .EnsureAsync("SysManager Privacy & Telemetry").ConfigureAwait(true);

        var failed = _service.ApplyAll(changed);
        var failedSet = failed.ToHashSet();

        // Only rebase the baseline for toggles that actually succeeded — a failed
        // (e.g. needs-elevation HKLM) toggle stays "pending" so the user sees it
        // wasn't applied rather than the change silently vanishing.
        var applied = changed.Where(t => !failedSet.Contains(t)).ToList();
        foreach (var t in applied)
            _baselineStates[t] = t.IsEnabled;
        RecomputePendingChanges();

        // Mentioned only when a point was actually created — Tweaks Hub's rule, verbatim. System
        // Restore is disabled by default on many consumer machines, and implying a safety net that
        // is not there would make this tab less safe than saying nothing.
        var rp = snapshotTaken ? " Restore point created." : "";

        if (failed.Count == 0)
        {
            StatusMessage = $"Applied {applied.Count} change{(applied.Count == 1 ? "" : "s")}.{rp}";
            Log.Information("Privacy: applied {Count} pending changes", applied.Count);
        }
        else
        {
            StatusMessage = $"Applied {applied.Count} change{(applied.Count == 1 ? "" : "s")}; " +
                $"{failed.Count} need administrator rights — relaunch as admin and try again.{rp}";
            Log.Warning("Privacy: {Applied} applied, {Failed} failed (likely elevation required)",
                applied.Count, failed.Count);
        }

        // Logged once for both branches, and only when something actually changed, so a partial apply
        // is recorded honestly rather than claiming the full count. Counts only — naming the toggles
        // would record which privacy settings this user cares about.
        if (applied.Count > 0)
        {
            ActivityLogService.Instance.Log("Privacy",
                failed.Count == 0
                    ? $"Applied {applied.Count} change{(applied.Count == 1 ? "" : "s")}"
                    : $"Applied {applied.Count} of {applied.Count + failed.Count} changes ({failed.Count} needed administrator)");
        }
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        foreach (var t in Toggles)
            if (_baselineStates.TryGetValue(t, out var baseline))
                t.IsEnabled = baseline;
        RecomputePendingChanges();
        StatusMessage = "Pending changes discarded.";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Read the registry off the UI thread — the service reads every privacy key
        // synchronously, which froze the UI when Refresh ran directly on the dispatcher.
        // Mirrors the async initial load (LoadTogglesAsync), including its progress feedback.
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var loaded = await Task.Run(_service.LoadToggles).ConfigureAwait(true);
            LoadToggles(loaded);
            StatusMessage = "Toggles refreshed from registry.";
            Log.Information("Privacy: refreshed toggle states from registry");
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    private void UpdateStatus()
    {
        var enabledCount = Toggles.Count(t => t.IsEnabled);
        var summary = $"{enabledCount} of {Toggles.Count} privacy protections active.";
        if (PendingChangeCount > 0)
            summary += $" {PendingChangeCount} pending change{(PendingChangeCount == 1 ? "" : "s")} — press Apply.";
        StatusMessage = summary;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var t in Toggles)
                t.PropertyChanged -= OnTogglePropertyChanged;
        }
        base.Dispose(disposing);
    }
}
