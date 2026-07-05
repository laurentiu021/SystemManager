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
/// ViewModel for the Debloater &amp; Ads tab. Lists removable Windows Store apps, lets the
/// user select them (with a curated "common bloat" preset), and removes the selection
/// per-user after an impact confirmation. System-critical packages are denylisted by the
/// service and shown disabled. Removal is reversible — apps can be reinstalled from the Store.
/// </summary>
public sealed partial class DebloaterViewModel : ViewModelBase
{
    private readonly DebloaterService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<StoreApp> Apps { get; } = new();
    public BulkObservableCollection<StoreApp> FilteredApps { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _hasApps;

    public DebloaterViewModel(DebloaterService service)
    {
        _service = service;
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
            Apps.ReplaceWith(apps);
            HasApps = apps.Count > 0;
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

        if (!DialogService.Instance.Confirm(
                $"Remove {targets.Count} app{(targets.Count == 1 ? "" : "s")} for the current user?\n\n{preview}\n\n" +
                "This uninstalls them for your account only. You can reinstall any of them later " +
                "from the Microsoft Store. Continue?",
                "Remove selected apps"))
        {
            StatusMessage = "Removal cancelled.";
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = false;
        Progress = 0;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var removed = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var app = targets[i];
                app.Status = "Removing…";
                StatusMessage = $"Removing {app.DisplayName} ({i + 1} of {targets.Count})…";
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
                Progress = (int)((i + 1) * 100.0 / targets.Count);
            }
            HasApps = Apps.Count > 0;
            ApplyFilter();
            StatusMessage = failed == 0
                ? $"Removed {removed} app{(removed == 1 ? "" : "s")}. Reinstall any from the Microsoft Store if needed."
                : $"Removed {removed}; {failed} could not be removed.";
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
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
