// SysManager · EnvironmentVariablesViewModel — view and edit Windows environment variables
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Environment Variables tab. Lists User and Machine (System)
/// variables, edits values in place, adds and removes variables, and offers a
/// dedicated PATH editor (reorder, remove duplicates, flag missing folders).
/// Edits are local until the user presses Apply; Machine-scope writes need admin.
/// </summary>
public sealed partial class EnvironmentVariablesViewModel : ViewModelBase
{
    private readonly EnvironmentVariableService _service;

    // Baseline of the on-disk state, keyed "scope\0NAME" → value. Used to compute the
    // pending-change count (edits + additions + deletions) the same way PrivacyViewModel does.
    private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);

    // Original (scope, name) pairs captured at load — lets us delete a removed variable by
    // its real name/scope even after its working item is gone from the list.
    private readonly Dictionary<string, (EnvVarScope scope, string name)> _originals = new(StringComparer.Ordinal);

    // True while the PATH editor writes back SelectedVariable.Value, so the resulting
    // PropertyChanged does not rebuild (and discard) the entries currently being edited.
    private bool _committingPath;

    public BulkObservableCollection<EnvVariable> Variables { get; } = new();
    public BulkObservableCollection<EnvVariable> FilteredVariables { get; } = new();

    /// <summary>Directories of the selected PATH-like variable, with missing/duplicate flags.</summary>
    public BulkObservableCollection<PathEntry> PathEntries { get; } = new();

    public List<string> ScopeFilters { get; } = ["All", "User", "System"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedScopeFilter = "All";
    [ObservableProperty] private bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private int _pendingChangeCount;

    public bool HasPendingChanges => PendingChangeCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPathSelected))]
    private EnvVariable? _selectedVariable;

    public bool IsPathSelected => SelectedVariable?.IsPathLike == true;

    // Add-new row.
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private string _newScope = "User";

    // Add-directory row (PATH editor).
    [ObservableProperty] private string _newDirectory = "";

    public EnvironmentVariablesViewModel(EnvironmentVariableService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        // Read both env hives off the UI thread so the eagerly-built VM doesn't block
        // startup; the UI update runs back on the UI thread (ConfigureAwait true).
        InitializeAsync(LoadAsync);
    }

    private static string Key(EnvVariable v) => Key(v.Scope, v.Name);
    private static string Key(EnvVarScope scope, string name) => $"{(int)scope}\0{name.ToUpperInvariant()}";

    private async Task LoadAsync()
    {
        var loaded = await Task.Run(_service.ReadAll).ConfigureAwait(true);
        Load(loaded);
    }

    private void Load(List<EnvVariable> loaded)
    {
        foreach (var v in Variables)
            v.PropertyChanged -= OnVariablePropertyChanged;

        Variables.ReplaceWith(loaded);

        _baseline.Clear();
        _originals.Clear();
        foreach (var v in Variables)
        {
            _baseline[Key(v)] = v.Value;
            _originals[Key(v)] = (v.Scope, v.Name);
            v.PropertyChanged += OnVariablePropertyChanged;
        }

        ApplyFilter();
        RecomputePending();
        UpdateStatus();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedScopeFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedVariableChanged(EnvVariable? oldValue, EnvVariable? newValue)
    {
        if (oldValue is not null)
            PathEntries.CollectionChanged -= OnPathEntriesChanged;
        RebuildPathEntries();
        if (IsPathSelected)
            PathEntries.CollectionChanged += OnPathEntriesChanged;
    }

    private void ApplyFilter()
    {
        IEnumerable<EnvVariable> source = Variables;

        if (SelectedScopeFilter == "User")
            source = source.Where(v => v.Scope == EnvVarScope.User);
        else if (SelectedScopeFilter == "System")
            source = source.Where(v => v.Scope == EnvVarScope.Machine);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            source = source.Where(v =>
                v.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                v.Value.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        FilteredVariables.ReplaceWith(source);
    }

    private void OnVariablePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EnvVariable.Value)) return;
        RecomputePending();
        UpdateStatus();
        // Rebuild the PATH editor only for an external edit (e.g. typing in the grid),
        // never for the editor's own write-back — that would discard the live entries.
        if (!_committingPath && ReferenceEquals(sender, SelectedVariable) && IsPathSelected)
            RebuildPathEntries();
    }

    private void RecomputePending()
    {
        var pending = 0;
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in Variables)
        {
            live.Add(Key(v));
            if (!_baseline.TryGetValue(Key(v), out var baseValue)) pending++;        // added
            else if (!string.Equals(baseValue, v.Value, StringComparison.Ordinal)) pending++; // edited
        }
        foreach (var key in _baseline.Keys)
            if (!live.Contains(key)) pending++;                                       // deleted

        PendingChangeCount = pending;
    }

    // ── PATH editor ────────────────────────────────────────────────────────────

    private void RebuildPathEntries()
    {
        if (!IsPathSelected || SelectedVariable is null)
        {
            PathEntries.Clear();
            return;
        }

        var dirs = EnvironmentVariableService.SplitPath(SelectedVariable.Value);
        PathEntries.ReplaceWith(dirs.Select(d => new PathEntry(d)));
        AnnotatePathEntries();
    }

    private void AnnotatePathEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in PathEntries)
        {
            var key = entry.Directory.TrimEnd('\\', '/');
            entry.IsDuplicate = !seen.Add(key);
            try { entry.IsMissing = !string.IsNullOrWhiteSpace(entry.Directory) && !Directory.Exists(entry.Directory); }
            catch (ArgumentException) { entry.IsMissing = true; } // malformed path characters
        }
    }

    private void OnPathEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => CommitPathEntries();

    /// <summary>Writes the current PATH-entry order back into the selected variable's value.</summary>
    private void CommitPathEntries()
    {
        if (!IsPathSelected || SelectedVariable is null) return;
        _committingPath = true;
        try { SelectedVariable.Value = EnvironmentVariableService.JoinPath(PathEntries.Select(p => p.Directory)); }
        finally { _committingPath = false; }
        AnnotatePathEntries();
    }

    [RelayCommand]
    private void AddDirectory()
    {
        var dir = NewDirectory.Trim();
        if (dir.Length == 0 || SelectedVariable is null) return;
        PathEntries.Add(new PathEntry(dir)); // triggers CommitPathEntries via CollectionChanged
        NewDirectory = "";
    }

    [RelayCommand]
    private void RemoveDirectory(PathEntry? entry)
    {
        if (entry is null) return;
        PathEntries.Remove(entry);
    }

    [RelayCommand]
    private void MoveDirectoryUp(PathEntry? entry)
    {
        if (entry is null) return;
        var i = PathEntries.IndexOf(entry);
        if (i > 0) PathEntries.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveDirectoryDown(PathEntry? entry)
    {
        if (entry is null) return;
        var i = PathEntries.IndexOf(entry);
        if (i >= 0 && i < PathEntries.Count - 1) PathEntries.Move(i, i + 1);
    }

    [RelayCommand]
    private void RemoveDuplicateDirectories()
    {
        if (!IsPathSelected || SelectedVariable is null) return;
        var deduped = EnvironmentVariableService.Deduplicate(PathEntries.Select(p => p.Directory));
        if (deduped.Count == PathEntries.Count)
        {
            StatusMessage = "No duplicate directories found.";
            return;
        }
        var removed = PathEntries.Count - deduped.Count;
        PathEntries.ReplaceWith(deduped.Select(d => new PathEntry(d)));
        CommitPathEntries();
        StatusMessage = $"Removed {removed} duplicate director{(removed == 1 ? "y" : "ies")} — press Apply to save.";
    }

    // ── Add / delete variables ───────────────────────────────────────────────────

    [RelayCommand]
    private void AddVariable()
    {
        string name;
        try { name = EnvironmentVariableService.ValidateName(NewName); }
        catch (ArgumentException ex) { StatusMessage = ex.Message; return; }

        var scope = NewScope == "System" ? EnvVarScope.Machine : EnvVarScope.User;
        if (Variables.Any(v => v.Scope == scope && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"A {NewScope} variable named '{name}' already exists.";
            return;
        }

        var added = new EnvVariable { Name = name, Scope = scope, Value = NewValue };
        added.PropertyChanged += OnVariablePropertyChanged;
        Variables.Add(added);
        ApplyFilter();
        RecomputePending();
        NewName = "";
        NewValue = "";
        StatusMessage = $"Added {NewScope} variable '{name}' — press Apply to save.";
    }

    [RelayCommand]
    private void DeleteVariable(EnvVariable? variable)
    {
        if (variable is null) return;
        if (!DialogService.Instance.Confirm(
                $"Remove the {variable.ScopeLabel} variable '{variable.Name}'?\n\n" +
                "It is removed locally now and deleted for real when you press Apply.",
                "Remove Environment Variable"))
            return;

        variable.PropertyChanged -= OnVariablePropertyChanged;
        Variables.Remove(variable);
        if (ReferenceEquals(variable, SelectedVariable)) SelectedVariable = null;
        ApplyFilter();
        RecomputePending();
        StatusMessage = $"Removed '{variable.Name}' locally — press Apply to delete it.";
    }

    // ── Apply / discard / refresh / restore ──────────────────────────────────────

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

        // Apply and Restore both rewrite the same HKCU/HKLM environment keys. Since 1.52.51 their
        // slow parts run off the UI thread, so without a shared gate a Restore in progress and an
        // Apply (or vice-versa) could write at once and leave a nondeterministic mix. Take the
        // app-wide system-modification lock, exactly like the SFC/DISM operations do.
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Apply environment changes");
        if (opLock is null)
        {
            StatusMessage = $"Cannot apply now — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
            return;
        }

        var touchesMachine = ChangedVariables().Any(v => v.Scope == EnvVarScope.Machine)
            || DeletedEntries().Any(e => e.scope == EnvVarScope.Machine);
        if (touchesMachine && !IsElevated)
        {
            StatusMessage = "Some changes affect System variables — relaunch as administrator first.";
            return;
        }

        if (!DialogService.Instance.Confirm(
                $"Apply {PendingChangeCount} environment change{(PendingChangeCount == 1 ? "" : "s")}?\n\n" +
                "A one-time backup of all variables is saved first so you can restore the original environment.",
                "Confirm Environment Changes"))
        {
            StatusMessage = "Apply cancelled.";
            return;
        }

        try { _service.EnsureBackup(); }
        catch (IOException ex)
        {
            StatusMessage = "Could not write the safety backup — no changes were made.";
            Log.Warning(ex, "Environment: backup write failed; aborting apply");
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = "Could not write the safety backup — no changes were made.";
            Log.Warning(ex, "Environment: backup write denied; aborting apply");
            return;
        }
        OnPropertyChanged(nameof(HasBackup));

        var failures = 0;
        var applied = 0;

        // Deletions: original variables no longer present in the working set.
        foreach (var (scope, name) in DeletedEntries().ToList())
        {
            if (_service.DeleteVariable(name, scope))
            {
                applied++;
                _baseline.Remove(Key(scope, name));
                _originals.Remove(Key(scope, name));
            }
            else failures++;
        }

        // Adds + edits.
        foreach (var v in ChangedVariables().ToList())
        {
            if (_service.SetVariable(v.Name, v.Value, v.Scope)) { applied++; _baseline[Key(v)] = v.Value; }
            else failures++;
        }

        // Broadcast once so running processes (Explorer, new shells) pick up the changes.
        // The broadcast waits up to 5 s for windows to respond, so run it OFF the UI thread —
        // the registry writes above are fast and stay on-thread (they own the change bookkeeping).
        if (applied > 0) await Task.Run(EnvironmentVariableService.BroadcastSettingChange);

        RecomputePending();

        if (failures == 0)
        {
            StatusMessage = $"Applied {applied} change{(applied == 1 ? "" : "s")}. Open a new terminal to see them.";
            Log.Information("Environment: applied {Count} changes", applied);
        }
        else
        {
            StatusMessage = $"Applied {applied} change{(applied == 1 ? "" : "s")}; {failures} need administrator rights.";
            Log.Warning("Environment: {Applied} applied, {Failed} failed (likely elevation required)", applied, failures);
        }
    }

    private IEnumerable<EnvVariable> ChangedVariables() => Variables.Where(v =>
        !_baseline.TryGetValue(Key(v), out var baseValue) ||
        !string.Equals(baseValue, v.Value, StringComparison.Ordinal));

    private IEnumerable<(EnvVarScope scope, string name)> DeletedEntries()
    {
        var live = Variables.Select(Key).ToHashSet(StringComparer.Ordinal);
        return _originals.Where(kv => !live.Contains(kv.Key)).Select(kv => kv.Value);
    }

    /// <summary>True once a pristine backup exists (after the first Apply) — enables Restore.</summary>
    public bool HasBackup => _service.HasBackup;

    [RelayCommand]
    private async Task RestoreBackup()
    {
        if (!_service.HasBackup)
        {
            StatusMessage = "There is no backup to restore yet — it is created the first time you apply changes.";
            return;
        }

        // Serialize against Apply (and other system-modification operations) so the two can't
        // rewrite the same environment keys concurrently — see ApplyChanges for the full note.
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Restore environment backup");
        if (opLock is null)
        {
            StatusMessage = $"Cannot restore now — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
            return;
        }

        // Restoring Machine-scope variables needs admin; warn early like Apply does.
        if (!IsElevated && _service.ReadBackup() is { Machine.Count: > 0 })
        {
            // Still allow restoring User-scope vars, but tell the user System ones will be skipped.
            if (!DialogService.Instance.Confirm(
                    "Restore the original environment from the backup?\n\n" +
                    "This rewrites every variable to its value from before your first change, and removes " +
                    "variables added since. System variables can only be restored when running as administrator " +
                    "and will be skipped now.",
                    "Restore Environment"))
            {
                StatusMessage = "Restore cancelled.";
                return;
            }
        }
        else if (!DialogService.Instance.Confirm(
                     "Restore the original environment from the backup?\n\n" +
                     "This rewrites every variable to its value from before your first change, and removes " +
                     "variables added since.",
                     "Restore Environment"))
        {
            StatusMessage = "Restore cancelled.";
            return;
        }

        // Restore rewrites many variables and then broadcasts (up to 5 s) — both run off the
        // UI thread; the list is re-read off-thread too via LoadAsync so the window stays live.
        var r = await Task.Run(_service.RestoreFromBackup);
        if (r.Restored > 0 || r.Removed > 0) await Task.Run(EnvironmentVariableService.BroadcastSettingChange);
        await LoadAsync();

        StatusMessage = r.Failed == 0
            ? $"Restored {r.Restored} variable{(r.Restored == 1 ? "" : "s")}" +
              (r.Removed > 0 ? $" and removed {r.Removed} added since." : ".")
            : $"Restored {r.Restored}, removed {r.Removed}; {r.Failed} need administrator rights.";
    }

    [RelayCommand]
    private async Task DiscardChanges()
    {
        await LoadAsync();
        StatusMessage = "Pending changes discarded.";
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await LoadAsync();
        StatusMessage = "Variables refreshed.";
        Log.Information("Environment: refreshed variable list");
    }

    private void UpdateStatus()
    {
        var userCount = Variables.Count(v => v.Scope == EnvVarScope.User);
        var sysCount = Variables.Count(v => v.Scope == EnvVarScope.Machine);
        var summary = $"{userCount} user · {sysCount} system variables.";
        if (PendingChangeCount > 0)
            summary += $" {PendingChangeCount} pending change{(PendingChangeCount == 1 ? "" : "s")} — press Apply.";
        StatusMessage = summary;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (IsPathSelected)
                PathEntries.CollectionChanged -= OnPathEntriesChanged;
            foreach (var v in Variables)
                v.PropertyChanged -= OnVariablePropertyChanged;
        }
        base.Dispose(disposing);
    }
}
