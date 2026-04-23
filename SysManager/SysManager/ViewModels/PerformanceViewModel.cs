// SysManager · PerformanceViewModel — performance mode tab
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Performance Mode tab — each tweak has its own Apply button.
/// Snapshot taken before first change; Restore All reverts everything.
///
/// SAFETY:
/// • Snapshot taken before first change — Restore reverts to exact original.
/// • Every toggle is two-door (enable ↔ disable).
/// • Confirmation dialog before every destructive action.
/// • GPU changes warn about reboot requirement.
/// </summary>
public partial class PerformanceViewModel : ViewModelBase
{
    private readonly PerformanceService _service;
    private PerformanceService.OriginalSnapshot? _snapshot;

    [ObservableProperty] private PerformanceProfile _profile = new();
    [ObservableProperty] private string _summary = "Click Refresh to read current performance settings.";

    // ── Desired state (set by UI, applied per-section) ──
    [ObservableProperty] private string _selectedPlan = "balanced";
    [ObservableProperty] private bool _wantVisualEffectsReduced;
    [ObservableProperty] private bool _wantGameModeOff;
    [ObservableProperty] private bool _wantXboxGameBarOff;
    [ObservableProperty] private bool _wantGpuMaxPerformance;
    [ObservableProperty] private bool _wantProcessorMaxState;

    // ── UI state ──
    [ObservableProperty] private bool _hasNvidiaGpu;
    [ObservableProperty] private string _nvidiaGpuName = "";
    [ObservableProperty] private bool _needsReboot;
    [ObservableProperty] private bool _hasSnapshot;

    public PerformanceViewModel(PowerShellRunner ps)
    {
        _service = new PerformanceService(ps);
        _ = RefreshAsync();
    }

    /// <summary>Ensure snapshot exists before any change.</summary>
    private async Task EnsureSnapshotAsync()
    {
        _snapshot ??= await _service.TakeSnapshotAsync();
        HasSnapshot = true;
    }

    private void UpdateSummary()
    {
        Summary = $"Active plan: {Profile.ProfileSummary} · "
                + $"Visual FX: {(Profile.VisualEffectsReduced ? "Reduced" : "Normal")} · "
                + $"Game Mode: {(Profile.GameModeEnabled ? "ON" : "OFF")} · "
                + $"Xbox Bar: {(Profile.XboxGameBarDisabled ? "OFF" : "ON")}";
    }

    // ═══════════════════════════════════════════════════════════════
    //  REFRESH
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Reading performance settings…";
        NeedsReboot = false;

        try
        {
            Profile = await _service.ReadProfileAsync();
            SyncTogglesFromProfile();
            UpdateSummary();
            StatusMessage = "Settings loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Summary = "Could not read performance settings.";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  POWER PLAN — separate Apply
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task ApplyPowerPlanAsync()
    {
        if (SelectedPlan == GetCurrentPlanKey())
        {
            StatusMessage = "Power plan is already set to the selected option.";
            return;
        }

        var planName = SelectedPlan switch
        {
            "ultimate" => "Ultimate Performance",
            "high" => "High Performance",
            _ => "Balanced"
        };

        var result = MessageBox.Show(
            $"Switch power plan to {planName}?",
            "Power Plan — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            await EnsureSnapshotAsync();
            StatusMessage = $"Switching to {planName}…";

            switch (SelectedPlan)
            {
                case "ultimate":
                    var guid = await _service.EnsureUltimatePerformancePlanAsync();
                    if (!string.IsNullOrEmpty(guid))
                        await _service.SetActivePlanAsync(guid);
                    break;
                case "high":
                    await _service.SetActivePlanAsync(PerformanceService.HighPerfGuid);
                    break;
                default:
                    await _service.SetActivePlanAsync(PerformanceService.BalancedGuid);
                    break;
            }

            await RefreshAsync();
            StatusMessage = $"Power plan set to {planName}.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  VISUAL EFFECTS — separate Apply
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task ApplyVisualEffectsAsync()
    {
        if (WantVisualEffectsReduced == Profile.VisualEffectsReduced)
        {
            StatusMessage = "Visual effects are already in the selected state.";
            return;
        }

        var action = WantVisualEffectsReduced ? "Reduce" : "Restore";
        var result = MessageBox.Show(
            $"{action} visual effects (animations, fades, shadows)?",
            "Visual Effects — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) { SyncTogglesFromProfile(); return; }

        try
        {
            await EnsureSnapshotAsync();
            PerformanceService.SetUiEffects(!WantVisualEffectsReduced);
            await RefreshAsync();
            StatusMessage = $"Visual effects {(WantVisualEffectsReduced ? "reduced" : "restored")}.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  GAME MODE — separate Apply
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task ApplyGameModeAsync()
    {
        var enabling = !WantGameModeOff;
        if (enabling == Profile.GameModeEnabled)
        {
            StatusMessage = "Game Mode is already in the selected state.";
            return;
        }

        var action = enabling ? "Enable" : "Disable";
        var result = MessageBox.Show(
            $"{action} Game Mode?",
            "Game Mode — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) { SyncTogglesFromProfile(); return; }

        try
        {
            await EnsureSnapshotAsync();
            PerformanceService.SetGameMode(enabling);
            await RefreshAsync();
            StatusMessage = $"Game Mode {(enabling ? "enabled" : "disabled")}.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  XBOX GAME BAR — separate Apply
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task ApplyXboxGameBarAsync()
    {
        var disabling = WantXboxGameBarOff;
        if (disabling == Profile.XboxGameBarDisabled)
        {
            StatusMessage = "Xbox Game Bar is already in the selected state.";
            return;
        }

        var action = disabling ? "Disable" : "Enable";
        var result = MessageBox.Show(
            $"{action} Xbox Game Bar and Game DVR overlay?",
            "Xbox Game Bar — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) { SyncTogglesFromProfile(); return; }

        try
        {
            await EnsureSnapshotAsync();
            PerformanceService.SetXboxGameBar(!disabling);
            await RefreshAsync();
            StatusMessage = $"Xbox Game Bar {(disabling ? "disabled" : "enabled")}.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  GPU — separate Apply
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task ApplyGpuAsync()
    {
        if (!HasNvidiaGpu)
        {
            StatusMessage = "No NVIDIA GPU detected.";
            return;
        }
        if (WantGpuMaxPerformance == Profile.GpuMaxPerformance)
        {
            StatusMessage = "GPU is already in the selected state.";
            return;
        }

        var action = WantGpuMaxPerformance ? "Enable" : "Disable";
        var result = MessageBox.Show(
            $"{action} NVIDIA GPU max performance (DisableDynamicPstate)?\n\n"
            + "⚠ This change requires a REBOOT to take effect.",
            "GPU Performance — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) { SyncTogglesFromProfile(); return; }

        try
        {
            await EnsureSnapshotAsync();
            var nvidiaKey = PerformanceService.FindNvidiaSubKey();
            if (nvidiaKey != null)
            {
                var ok = PerformanceService.SetGpuMaxPerformance(nvidiaKey, WantGpuMaxPerformance);
                if (ok)
                {
                    NeedsReboot = true;
                    StatusMessage = $"GPU max performance {(WantGpuMaxPerformance ? "enabled" : "disabled")}. Reboot required.";
                }
                else
                    StatusMessage = "Failed to write GPU registry key (admin required).";
            }
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  PROCESSOR STATE — separate Apply
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task ApplyProcessorStateAsync()
    {
        if (WantProcessorMaxState == Profile.ProcessorMaxState)
        {
            StatusMessage = "Processor state is already in the selected state.";
            return;
        }

        var action = WantProcessorMaxState ? "Set to 100%" : "Restore default";
        var result = MessageBox.Show(
            $"{action} processor minimum state?",
            "Processor State — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) { SyncTogglesFromProfile(); return; }

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            await EnsureSnapshotAsync();
            var target = WantProcessorMaxState ? 100 : _snapshot!.ProcessorMinPercentAc;
            await _service.SetProcessorMinStateAsync(target);
            await RefreshAsync();
            StatusMessage = $"Processor min state set to {target}%.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  RESTORE ALL — revert everything to snapshot
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        if (_snapshot == null)
        {
            StatusMessage = "Nothing to restore — no changes have been applied yet.";
            return;
        }

        var gpuWasChanged = _snapshot.NvidiaSubKey != null
            && Profile.GpuMaxPerformance != !_snapshot.GpuDynamicPstate;

        var result = MessageBox.Show(
            "Restore ALL settings to the state before any changes were made?\n\n"
            + $"• Power plan → {_snapshot.PowerPlanName}\n"
            + $"• Visual effects → {(_snapshot.UiEffectsEnabled ? "Normal" : "Reduced")}\n"
            + $"• Game Mode → {(_snapshot.GameModeEnabled ? "ON" : "OFF")}\n"
            + $"• Xbox Game Bar → {(_snapshot.XboxGameBarEnabled ? "ON" : "OFF")}\n"
            + $"• Processor min state → {_snapshot.ProcessorMinPercentAc}%\n"
            + (gpuWasChanged ? "• GPU → Dynamic P-state (reboot needed)\n" : "")
            + "\nContinue?",
            "Restore Original Settings — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Restoring original settings…";

        try
        {
            await _service.RestoreFromSnapshotAsync(_snapshot);
            NeedsReboot = gpuWasChanged;
            StatusMessage = NeedsReboot
                ? "Original settings restored. Reboot required for GPU changes."
                : "Original settings restored.";
            _snapshot = null;
            HasSnapshot = false;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════

    private string GetCurrentPlanKey()
    {
        if (Profile.ActivePlanName.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
            return "ultimate";
        if (Profile.ActivePlanGuid.Contains("8c5e7fda", StringComparison.OrdinalIgnoreCase))
            return "high";
        return "balanced";
    }

    private void SyncTogglesFromProfile()
    {
        SelectedPlan = GetCurrentPlanKey();
        WantVisualEffectsReduced = Profile.VisualEffectsReduced;
        WantGameModeOff = !Profile.GameModeEnabled;
        WantXboxGameBarOff = Profile.XboxGameBarDisabled;
        WantGpuMaxPerformance = Profile.GpuMaxPerformance;
        WantProcessorMaxState = Profile.ProcessorMaxState;
        HasNvidiaGpu = Profile.HasNvidiaGpu;
        NvidiaGpuName = Profile.NvidiaGpuName;
    }
}
