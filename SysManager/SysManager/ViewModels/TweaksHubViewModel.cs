// SysManager · TweaksHubViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Tweaks Hub tab — a unified front-end over SysManager's existing
/// reversible optimizations, grouped into Essential (per-user, safe) and Advanced
/// (machine-wide, needs admin). The user ticks tweaks and applies or reverts them in bulk;
/// a restore point is taken before the first change. Nothing is written without an explicit
/// Apply/Undo, each confirmed first. Delegates to <see cref="TweaksHubService"/>.
/// </summary>
public sealed partial class TweaksHubViewModel : ViewModelBase
{
    private readonly ITweaksHubService _service;

    public BulkObservableCollection<TweakItem> Essential { get; } = new();
    public BulkObservableCollection<TweakItem> Advanced { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private int _pendingApply;
    [ObservableProperty] private int _pendingUndo;

    public TweaksHubViewModel(ITweaksHubService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        Load();
    }

    private IEnumerable<TweakItem> AllTweaks => Essential.Concat(Advanced);

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private void Refresh() => Load();

    private void Load()
    {
        // Detach old selection-change handlers before replacing the items.
        foreach (var t in AllTweaks) t.PropertyChanged -= OnTweakChanged;

        var tweaks = _service.LoadTweaks();
        Essential.ReplaceWith(tweaks.Where(t => t.Tier == TweakTier.Essential));
        Advanced.ReplaceWith(tweaks.Where(t => t.Tier == TweakTier.Advanced));

        foreach (var t in AllTweaks) t.PropertyChanged += OnTweakChanged;
        RecountPending();
        StatusMessage = $"{Essential.Count} essential · {Advanced.Count} advanced tweak(s). Tick the ones you want, then Apply or Undo.";
    }

    private void OnTweakChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TweakItem.IsSelected) or nameof(TweakItem.IsApplied))
            RecountPending();
    }

    private void RecountPending()
    {
        PendingApply = TweaksHubService.PendingApplyCount(AllTweaks);
        PendingUndo = TweaksHubService.PendingUndoCount(AllTweaks);
        ApplySelectedCommand.NotifyCanExecuteChanged();
        UndoSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply() => PendingApply > 0;
    private bool CanUndo() => PendingUndo > 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplySelectedAsync()
    {
        var toApply = AllTweaks.Where(t => t.IsSelected && !t.IsApplied).ToList();
        if (toApply.Count == 0) return;

        if (!DialogService.Instance.Confirm(
                $"Apply {toApply.Count} selected tweak(s)?\n\n" +
                "SysManager will try to create a System Restore point first (when running as " +
                "administrator), and each tweak is individually reversible from here.",
                "Apply Tweaks — Confirm"))
            return;

        await RunBatchAsync(toApply, enable: true, "Applied");
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoSelectedAsync()
    {
        var toUndo = AllTweaks.Where(t => t.IsSelected && t.IsApplied).ToList();
        if (toUndo.Count == 0) return;

        if (!DialogService.Instance.Confirm(
                $"Undo {toUndo.Count} selected tweak(s), restoring the Windows default?",
                "Undo Tweaks — Confirm"))
            return;

        await RunBatchAsync(toUndo, enable: false, "Reverted");
    }

    private async Task RunBatchAsync(IReadOnlyList<TweakItem> items, bool enable, string verb)
    {
        IsBusy = true;
        try
        {
            var result = await _service.ApplyAsync(items, enable);
            int failedCount = result.Failed.Count;
            int ok = items.Count - failedCount;
            if (ok > 0) ActivityLogService.Instance.Log("Tweaks Hub", $"{verb} {ok} tweak(s)");
            Log.Information("Tweaks Hub {Verb}: {Ok} ok, {Failed} failed, restorePoint={Rp}",
                verb, ok, failedCount, result.RestorePointCreated);

            // Mention the restore point only when one was actually created — never claim a
            // safety net that silently didn't materialize (non-admin / rate-limited / SR off).
            var rp = enable && result.RestorePointCreated ? " Restore point created." : "";
            StatusMessage = failedCount == 0
                ? $"{verb} {ok} tweak(s).{rp}"
                : $"{verb} {ok} tweak(s) · {failedCount} need administrator (run as admin and retry).{rp}";
            RecountPending();
        }
        finally { IsBusy = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var t in AllTweaks) t.PropertyChanged -= OnTweakChanged;
        base.Dispose(disposing);
    }
}
