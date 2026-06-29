// SysManager · SettingsWatchdogViewModel
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
/// ViewModel for the Settings Watchdog tab. The user saves a baseline of preferences that
/// Windows Update tends to reset; the watchdog later re-reads the live values and lists any
/// drift in plain language, offering one-click restore. Read-only until the user explicitly
/// saves a baseline or restores a setting; restore writes only well-known registry values.
/// </summary>
public sealed partial class SettingsWatchdogViewModel : ViewModelBase
{
    private readonly SettingsWatchdogService _service;

    public BulkObservableCollection<DriftRow> Drifts { get; } = new();
    public BulkObservableCollection<WatchedSetting> Watched { get; } = new();

    [ObservableProperty] private bool _hasBaseline;
    [ObservableProperty] private string _baselineTaken = "";
    [ObservableProperty] private bool _hasDrift;
    [ObservableProperty] private bool _isElevated;

    public SettingsWatchdogViewModel(SettingsWatchdogService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        Watched.ReplaceWith(_service.Catalog);
        InitializeAsync(() => { Refresh(); return System.Threading.Tasks.Task.CompletedTask; });
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>Re-reads the baseline and live state and rebuilds the drift list.</summary>
    [RelayCommand]
    private void Refresh()
    {
        var baseline = _service.LoadBaseline();
        HasBaseline = baseline is not null;
        BaselineTaken = baseline is not null ? $"Baseline saved {baseline.TakenAt:yyyy-MM-dd HH:mm}" : "";

        var drifts = _service.DetectDrift();
        Drifts.ReplaceWith(drifts.Select(d => new DriftRow(d)));
        HasDrift = Drifts.Count > 0;
        RestoreSelectedCommand.NotifyCanExecuteChanged();

        StatusMessage = !HasBaseline
            ? "No baseline yet — save your current settings to start watching for changes."
            : HasDrift
                ? $"{Drifts.Count} setting(s) changed since your baseline."
                : "All watched settings match your baseline.";
    }

    /// <summary>Captures the current settings as the new baseline (with confirmation).</summary>
    [RelayCommand]
    private void SaveBaseline()
    {
        if (HasBaseline && !DialogService.Instance.Confirm(
                "Overwrite your saved baseline with the current settings?\n\n" +
                "Any current drift will be accepted as the new normal.",
                "Save Baseline — Confirm"))
            return;

        _service.SaveBaseline(DateTime.Now);
        ActivityLogService.Instance.Log("Settings Watchdog", "Saved settings baseline");
        Refresh();
        StatusMessage = "Baseline saved. The watchdog will flag future changes.";
    }

    private bool CanRestore() => Drifts.Any(d => d.Drift.CanRestore);

    /// <summary>Restores every restorable drifted setting to its baseline value (with confirmation).</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void RestoreSelected()
    {
        var restorable = Drifts.Where(d => d.Drift.CanRestore).ToList();
        if (restorable.Count == 0) return;

        if (!DialogService.Instance.Confirm(
                $"Restore {restorable.Count} setting(s) to your saved baseline?\n\n" +
                "Each will be written back to the value it had when you saved the baseline.",
                "Restore Settings — Confirm"))
            return;

        int restored = 0, failed = 0;
        foreach (var row in restorable)
        {
            if (_service.Restore(row.Drift)) restored++; else failed++;
        }

        if (restored > 0)
            ActivityLogService.Instance.Log("Settings Watchdog", $"Restored {restored} setting(s) to baseline");
        Log.Information("Settings Watchdog restore: {Restored} ok, {Failed} failed", restored, failed);

        Refresh();
        StatusMessage = failed == 0
            ? $"Restored {restored} setting(s) to your baseline."
            : $"Restored {restored} setting(s) · {failed} could not be written (try running as administrator).";
    }

    /// <summary>One drifted setting, wrapping the immutable <see cref="SettingDrift"/> for binding.</summary>
    public sealed partial class DriftRow(SettingDrift drift) : ObservableObject
    {
        public SettingDrift Drift { get; } = drift;
        public string Name => Drift.Setting.Name;
        public string Category => Drift.Setting.Category;
        public string Description => Drift.Setting.Description;
        public string BaselineLabel => Drift.BaselineLabel;
        public string CurrentLabel => Drift.CurrentLabel;
        public bool CanRestore => Drift.CanRestore;
    }
}
