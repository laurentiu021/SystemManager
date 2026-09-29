// SysManager · DebloaterViewModel — list and remove preinstalled Store apps safely
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
/// ViewModel for the Preinstalled Apps tab. Lists removable Windows Store apps, lets the
/// user select them (with a curated "common bloat" preset), and removes the selection
/// per-user after an impact confirmation. System-critical packages are denylisted by the
/// service and shown disabled. Removal is reversible for most apps — they can be reinstalled from the Store —
/// but not for one Microsoft has retired, which the confirmation and the result name.
/// A session restore point is taken before the first removal, but System Restore does not bring
/// Appx packages back, so the Store remains the real undo and the copy says so.
/// </summary>
public sealed partial class DebloaterViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelCommand : null;

    private readonly DebloaterService _service;
    private readonly ISessionRestorePoint _restorePoint;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<StoreApp> Apps { get; } = new();
    public BulkObservableCollection<StoreApp> FilteredApps { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _hasApps;

    // Distinguishes "never scanned" from "scanned, zero results" so the centered empty
    // state stops telling the user to Refresh after a scan that already returned nothing
    // (which contradicted the status bar's "No Store apps found").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyMessage))]
    private bool _hasScanned;

    // Distinguishes "Windows answered with none" from "the read failed", so a failure is never reported as a PC
    // with no Store apps. It used to be: the service returned an empty list for a failed read (#2487).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyMessage))]
    private bool _listFailed;

    public string EmptyTitle => ListFailed ? "Installed apps could not be read"
        : HasScanned ? "No Store apps found" : "No apps loaded";

    public string EmptyMessage => ListFailed
        ? "Windows did not answer when SysManager asked for the installed apps. Press Refresh to try again."
        : HasScanned
            ? "There are no removable Store apps on this system."
            : "Press Refresh to scan installed Store apps.";

    public DebloaterViewModel(DebloaterService service, ISessionRestorePoint restorePoint)
    {
        _service = service;
        _restorePoint = restorePoint;
        StatusMessage = "Loading installed apps…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }

    private bool NotBusy => !IsBusy;

    /// <summary>
    /// Remove/select/clear only make sense once a scan has produced apps. Gating
    /// CanExecute on <see cref="HasApps"/> keeps the destructive DangerButton visibly
    /// disabled on an empty list instead of red-and-clickable with nothing to act on.
    /// </summary>
    private bool CanActOnApps => NotBusy && HasApps;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy) or nameof(HasApps))
        {
            RefreshCommand.NotifyCanExecuteChanged();
            RemoveSelectedCommand.NotifyCanExecuteChanged();
            SelectCommonBloatCommand.NotifyCanExecuteChanged();
            ClearSelectionCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<StoreApp> source = Apps;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            source = source.Where(a =>
                a.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                a.Publisher.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        FilteredApps.ReplaceWith(source);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Reading installed Store apps…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var apps = await _service.ListAsync(_cts.Token).ConfigureAwait(true);
            if (apps is null)
            {
                // A failed read changes nothing on screen: what was listed stays listed, and the empty state and
                // the status line say the read failed rather than that there are no apps.
                ListFailed = true;
                StatusMessage = Apps.Count == 0
                    ? "Could not read the installed apps. Press Refresh to try again."
                    : "Could not read the installed apps, so the list below is from the last scan.";
                return;
            }
            ListFailed = false;
            // Keep the user's ticks across the rescan. These arrive UNSELECTED, so a rescan cleared the
            // selection rather than reversing it — the Remove button simply stopped doing anything until
            // every app was ticked again. Less dangerous than the tabs whose rows arrive pre-selected, but
            // the same defect, and RefreshOnF5 is RefreshCommand (#2304).
            //
            // Keyed on the package FAMILY name, not the full name: the full name carries the version, so
            // an app updating between two scans would look like a different app and silently lose the tick.
            // The family name is what stays the same across versions.
            Helpers.SelectionCarry.Apply(Apps, apps, a => a.PackageFamilyName, StringComparer.OrdinalIgnoreCase);
            Apps.ReplaceWith(apps);
            HasApps = apps.Count > 0;
            HasScanned = true;
            ApplyFilter();
            var removable = apps.Count(a => !a.IsProtected);
            StatusMessage = apps.Count == 0
                ? "No Store apps found."
                : $"{apps.Count} apps ({removable} removable, {apps.Count - removable} protected).";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnApps))]
    private void SelectCommonBloat()
    {
        var n = 0;
        foreach (var app in Apps)
        {
            app.IsSelected = app.IsCommonBloat;
            if (app.IsSelected) n++;
        }
        StatusMessage = $"Selected {n} commonly-removed app{(n == 1 ? "" : "s")}. Review, then Remove selected.";
    }

    [RelayCommand(CanExecute = nameof(CanActOnApps))]
    private void ClearSelection()
    {
        foreach (var app in Apps) app.IsSelected = false;
        StatusMessage = "Selection cleared.";
    }

    [RelayCommand(CanExecute = nameof(CanActOnApps))]
    private async Task RemoveSelectedAsync()
    {
        // Protected packages can never be selected for removal, even if a binding tries.
        var targets = Apps.Where(a => a.IsSelected && !a.IsProtected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "Nothing selected. Tick the apps to remove, or use the preset.";
            return;
        }

        var preview = string.Join("\n", targets.Take(15).Select(a => $"  • {a.DisplayName}"));
        if (targets.Count > 15) preview += $"\n  …and {targets.Count - 15} more";

        // An app Microsoft has retired is not in the Store any more, so the prompt cannot promise it back (#2505).
        var retired = targets.Where(a => a.IsRetired).Select(a => a.DisplayName).ToList();
        var reinstall = retired.Count == 0
            ? "You can reinstall any of them later from the Microsoft Store."
            : $"{FormatHelper.JoinForSentence(retired)} cannot be reinstalled afterwards: Microsoft has retired "
              + (retired.Count == 1 ? "it." : "them.")
              + (retired.Count < targets.Count ? " The others can be reinstalled later from the Microsoft Store." : "");

        if (!DialogService.Instance.Confirm(
                $"Remove {targets.Count} app{(targets.Count == 1 ? "" : "s")} for the current user?\n\n{preview}\n\n" +
                "This uninstalls them for your account only. " + reinstall + _restorePoint.ConfirmationNotice,
                "Remove selected apps"))
        {
            StatusMessage = "Removal cancelled.";
            return;
        }

        // A removal batch that is cancelled with nothing to name it when SysManager closes mid-run. The same
        // lock the other tabs that take a restore point before changing the system already share (#2510).
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Preinstalled Apps");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
            return;
        }

        IsBusy = true;
        // The snapshot takes seconds and reports no percentage, so the bar stays a marquee until it
        // is done and only then switches to the determinate per-app progress below. Deliberately
        // wordless: announcing the attempt would promise a point Windows refuses more often than it
        // grants one, and nothing would retract it.
        IsProgressIndeterminate = true;
        Progress = 0;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var removed = 0;
        var failed = 0;
        try
        {
            // Before the first removal, never after. Best-effort by design: Windows allows roughly
            // one point per 24h and System Restore is off entirely on many consumer machines, so the
            // removal must not be gated on it — and must never be claimed unless it really happened.
            var snapshotTaken = await _restorePoint
                .EnsureAsync("SysManager Debloater", _cts.Token).ConfigureAwait(true);
            IsProgressIndeterminate = false;

            for (var i = 0; i < targets.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var app = targets[i];
                app.Status = "Removing…";
                StatusMessage = $"Removing {app.DisplayName} ({i + 1} of {targets.Count})…";
                try
                {
                    var ok = await _service.RemoveAsync(app, _cts.Token).ConfigureAwait(true);
                    if (ok)
                    {
                        removed++;
                        app.Status = "Removed";
                        Apps.Remove(app);
                    }
                    else
                    {
                        failed++;
                        app.Status = "Failed";
                    }
                }
                // RemoveAsync runs PowerShell; a runspace-level fault (runspace-open failure,
                // PSInvalidOperationException → InvalidOperationException, or a Win32Exception
                // launching the host) is NOT the RuntimeException the service catches, so it
                // would otherwise escape this loop, abort the whole batch mid-way, and hit the
                // global dispatcher MessageBox — leaving remaining rows frozen at "Removing…".
                // Fail just this row and continue, mirroring DefenderViewModel's guard.
                catch (InvalidOperationException ex)
                {
                    failed++;
                    app.Status = "Failed";
                    Log.Warning(ex, "Debloater: removal of {App} faulted", app.DisplayName);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    failed++;
                    app.Status = "Failed";
                    Log.Warning(ex, "Debloater: removal of {App} faulted", app.DisplayName);
                }
                Progress = (int)((i + 1) * 100.0 / targets.Count);
            }
            HasApps = Apps.Count > 0;
            ApplyFilter();
            // The Store reinstall leads because it is the reassurance that is actually true here:
            // System Restore does NOT restore removed Appx packages. The point is mentioned second
            // and scoped to what it really covers, so it cannot be read as "your apps are safe".
            var rp = snapshotTaken ? " A restore point covers the rest of the system, not the apps themselves." : "";
            var gone = targets.Where(a => a.IsRetired && a.Status == "Removed").Select(a => a.DisplayName).ToList();
            var store = gone.Count == 0
                ? " Reinstall any from the Microsoft Store if needed."
                : gone.Count == removed
                    ? $" {FormatHelper.JoinForSentence(gone)} cannot be reinstalled: Microsoft has retired "
                      + (gone.Count == 1 ? "it." : "them.")
                    : $" Reinstall the others from the Microsoft Store if needed. {FormatHelper.JoinForSentence(gone)} "
                      + $"cannot be reinstalled: Microsoft has retired {(gone.Count == 1 ? "it" : "them")}.";
            StatusMessage = failed == 0
                ? $"Removed {removed} app{(removed == 1 ? "" : "s")}.{store}{rp}"
                : $"Removed {removed}; {failed} could not be removed.{rp}";
            Log.Information("Debloater: removed {Removed}, failed {Failed}", removed, failed);
            if (removed > 0)
                ActivityLogService.Instance.Log("Debloater", $"Removed {removed} app{(removed == 1 ? "" : "s")}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Cancelled after removing {removed} app{(removed == 1 ? "" : "s")}.";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
