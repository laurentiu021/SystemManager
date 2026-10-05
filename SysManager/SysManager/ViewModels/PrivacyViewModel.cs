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
/// <para>Privacy choices imported from a profile arrive through <see cref="IPrivacyChoicesHandoff"/> and are
/// staged the same way a click is: the switches move, the pending count says how many, and nothing is
/// written until Apply (#1530).</para>
/// </summary>
public sealed partial class PrivacyViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    private readonly IPrivacyService _service;
    private readonly ISessionRestorePoint _restorePoint;
    private readonly IPrivacyChoicesHandoff _importedChoices;
    private readonly Dictionary<PrivacyToggle, bool> _baselineStates = [];

    /// <summary>True while Apply waits for the restore point, the one moment another tab can be opened mid-apply.</summary>
    private bool _awaitingRestorePoint;

    public BulkObservableCollection<PrivacyToggle> Toggles { get; } = new();

    [ObservableProperty] private List<string> _categories = [];
    [ObservableProperty] private string _selectedCategory = "All";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private int _pendingChangeCount;

    public bool HasPendingChanges => PendingChangeCount > 0;

    /// <summary>
    /// True while the tab is on screen. Set by <see cref="MainWindowViewModel.SetActive"/>.
    /// </summary>
    /// <remarks>
    /// Becoming visible stages any privacy choices a profile import left for this tab. The import sends the user
    /// here as it finishes, and this view model may have been built long before, with its toggles loaded.
    /// </remarks>
    [ObservableProperty] private bool _isActive;

    public BulkObservableCollection<PrivacyToggle> FilteredToggles { get; } = new();

    public PrivacyViewModel(IPrivacyService service, ISessionRestorePoint restorePoint, IPrivacyChoicesHandoff importedChoices)
    {
        _service = service;
        _restorePoint = restorePoint;
        _importedChoices = importedChoices;
        IsElevated = AdminHelper.IsElevated();
        // Read the registry-backed toggles off the UI thread so the eagerly-built VM
        // doesn't block startup; the UI update runs back on the UI thread (ConfigureAwait true).
        InitializeAsync(LoadTogglesAsync);
    }

    partial void OnIsActiveChanged(bool value)
    {
        // Not while a load is running, first or Refresh: it is about to replace every toggle, and it stages the
        // choices itself once it has (LoadToggles), so staging them now would put them on toggles that are
        // about to be thrown away. Nor while Apply waits for its restore point; it stages them when it is done.
        if (value && InitializationComplete.IsCompleted && !IsBusy && !_awaitingRestorePoint)
            StageImportedChoices();
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
        StageImportedChoices();
    }

    /// <summary>
    /// Stages the privacy choices a profile import left for this tab, if there are any: each switch the
    /// profile names moves to the profile's position as a pending change, and nothing is written.
    /// </summary>
    /// <remarks>
    /// A switch the profile does not name keeps whatever the user had, including an unapplied click. The
    /// baseline stays the registry reading, so Discard puts this PC's own settings back and Apply writes
    /// through the same confirmation and restore point as any other change.
    /// </remarks>
    private void StageImportedChoices()
    {
        if (_importedChoices.Take() is not { } choices) return;

        // Every category, or a change the profile made under a filtered-out one would sit where nobody sees it.
        SelectedCategory = "All";

        var changes = 0;
        var needAdmin = 0;
        foreach (var toggle in Toggles)
        {
            if (!choices.Protections.TryGetValue(toggle.Key, out var on)) continue;
            toggle.IsEnabled = on;
            if (!_baselineStates.TryGetValue(toggle, out var baseline) || baseline == on) continue;
            changes++;
            if (TweakItem.ClassifyTier(toggle.RegistryPath) == TweakTier.Advanced) needAdmin++;
        }

        StatusMessage = DescribeImportedChoices(changes, needAdmin, IsElevated);
        Log.Information("Privacy: staged {Changes} change(s) from an imported profile, {NeedAdmin} needing administrator",
            changes, needAdmin);
    }

    /// <summary>
    /// What the tab says once imported choices are staged: how many switches the profile moved, that nothing has
    /// changed yet, and, when SysManager is not elevated, how many of those changes Apply cannot write.
    /// </summary>
    /// <remarks>
    /// The administrator advice says to import again because the choices do not survive the restart: relaunching
    /// as administrator starts a new SysManager, and a staged change is only a switch position in this one.
    /// </remarks>
    internal static string DescribeImportedChoices(int changes, int needAdmin, bool elevated)
    {
        if (changes == 0)
            return "The imported privacy choices already match this PC, so there is nothing to change.";

        var text = $"The imported profile changes {changes} privacy setting{(changes == 1 ? "" : "s")} on this PC. "
            + "Nothing has changed yet: check the switches, then press Apply, or Discard to keep this PC as it is.";
        if (needAdmin == 0 || elevated)
            return text;

        return text + (needAdmin == changes
            ? (changes == 1 ? " It needs" : " They all need")
              + " administrator rights, so run SysManager as administrator and import the profile again to apply "
              + (changes == 1 ? "it." : "them.")
            : $" {needAdmin} of them need{(needAdmin == 1 ? "s" : "")} administrator rights. To apply "
              + (needAdmin == 1 ? "that one" : "those")
              + " too, run SysManager as administrator and import the profile again.");
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
            App.RequestShutdown();
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
                "Each toggle can be reverted by switching it back and pressing Apply again." +
                _restorePoint.ConfirmationNotice,
                "Confirm Privacy Changes"))
        {
            StatusMessage = "Apply cancelled.";
            return;
        }

        // A batch that is cancelled with nothing to name it when SysManager closes mid-run. Shares the
        // lock the other tabs that take a restore point before changing the system already do (#2510).
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Privacy & Telemetry");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
            return;
        }

        // Before the write, never after: a snapshot taken afterwards would record the state the
        // user is trying to be able to get back FROM. Taken after the confirmation, so declining
        // costs nothing, and it is the same seam Tweaks Hub uses rather than a second copy.
        // The point can take long enough to open another tab and come back, so an import that arrives
        // meanwhile must not move switches this apply is about to write: it is staged at the end instead.
        bool snapshotTaken;
        _awaitingRestorePoint = true;
        try
        {
            snapshotTaken = await _restorePoint
                .EnsureAsync("SysManager Privacy & Telemetry").ConfigureAwait(true);
        }
        finally { _awaitingRestorePoint = false; }

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

        // An import that arrived while the restore point was being taken, now that the write is done.
        if (IsActive) StageImportedChoices();
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
