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
/// </summary>
public sealed partial class PrivacyViewModel : ViewModelBase
{
    private readonly PrivacyService _service;
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

    public PrivacyViewModel(PrivacyService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        LoadToggles();
    }

    private void LoadToggles()
    {
        // Unsubscribe from old toggles
        foreach (var t in Toggles)
            t.PropertyChanged -= OnTogglePropertyChanged;

        var loaded = _service.LoadToggles();
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
    private void ApplyChanges()
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

        var failed = _service.ApplyAll(changed);
        var failedSet = failed.ToHashSet();

        // Only rebase the baseline for toggles that actually succeeded — a failed
        // (e.g. needs-elevation HKLM) toggle stays "pending" so the user sees it
        // wasn't applied rather than the change silently vanishing.
        var applied = changed.Where(t => !failedSet.Contains(t)).ToList();
        foreach (var t in applied)
            _baselineStates[t] = t.IsEnabled;
        RecomputePendingChanges();

        if (failed.Count == 0)
        {
            StatusMessage = $"Applied {applied.Count} change{(applied.Count == 1 ? "" : "s")}.";
            Log.Information("Privacy: applied {Count} pending changes", applied.Count);
        }
        else
        {
            StatusMessage = $"Applied {applied.Count} change{(applied.Count == 1 ? "" : "s")}; " +
                $"{failed.Count} need administrator rights — relaunch as admin and try again.";
            Log.Warning("Privacy: {Applied} applied, {Failed} failed (likely elevation required)",
                applied.Count, failed.Count);
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
    private void Refresh()
    {
        LoadToggles();
        StatusMessage = "Toggles refreshed from registry.";
        Log.Information("Privacy: refreshed toggle states from registry");
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
