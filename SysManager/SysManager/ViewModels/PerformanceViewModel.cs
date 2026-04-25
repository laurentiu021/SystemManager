// SysManager · PerformanceViewModel — performance mode tab
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Security;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessorStateEditable))]
    private bool _isProcessorStateLocked;

    /// <summary>Inverse of <see cref="IsProcessorStateLocked"/> for XAML IsEnabled bindings.</summary>
    public bool IsProcessorStateEditable => !IsProcessorStateLocked;

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
            IsHibernationEnabled = PerformanceService.ReadHibernationEnabled();
            UpdateSummary();
            StatusMessage = "Settings loaded.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Summary = "Could not read performance settings.";
        }
        catch (SecurityException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Summary = "Could not read performance settings.";
        }
        catch (UnauthorizedAccessException ex)
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
            Log.Information("Power plan changed to {PlanName}", planName);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
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
            Log.Information("Visual effects {Action}", WantVisualEffectsReduced ? "reduced" : "restored");
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
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
            Log.Information("Game Mode {Action}", enabling ? "enabled" : "disabled");
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
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
            Log.Information("Xbox Game Bar {Action}", disabling ? "disabled" : "enabled");
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
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
                    Log.Information("GPU max performance {Action}. Reboot required", WantGpuMaxPerformance ? "enabled" : "disabled");
                }
                else
                    StatusMessage = "Failed to write GPU registry key (admin required).";
            }
            await RefreshAsync();
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  PROCESSOR STATE — separate Apply
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task ApplyProcessorStateAsync()
    {
        if (IsProcessorStateLocked)
        {
            StatusMessage = "Processor state is locked to 100 % by the current power plan. Switch to Balanced to adjust it.";
            return;
        }

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
            Log.Information("Processor min state set to {Percent}%", target);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  RESTORE POINT — create a system restore point
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task CreateRestorePointAsync()
    {
        if (!Helpers.AdminHelper.IsElevated())
        {
            StatusMessage = "⚠ Creating a restore point requires administrator privileges.";
            return;
        }

        var result = MessageBox.Show(
            "Create a System Restore point?\n\nThis saves the current system state so you can roll back later if something goes wrong.",
            "Restore Point — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Creating restore point…";
        try
        {
            var ok = await _service.CreateRestorePointAsync(
                $"SysManager — {DateTime.Now:yyyy-MM-dd HH:mm}");
            StatusMessage = ok
                ? "✓ Restore point created successfully."
                : "✗ Failed to create restore point. Check Event Viewer for details.";
            if (ok) Log.Information("System restore point created");
            else Log.Warning("System restore point creation failed");
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  RAM WORKING SET TRIM
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private void TrimRam()
    {
        var result = MessageBox.Show(
            "Trim the working set of all processes?\n\n"
            + "This frees physical RAM by moving pages to the standby list. "
            + "No data is lost — pages are soft-faulted back on demand. "
            + "Apps may feel briefly slower on next access.\n\n"
            + "This is the same as \"Empty Working Set\" in RAMMap.",
            "RAM Trim — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            var count = PerformanceService.TrimWorkingSets();
            StatusMessage = $"✓ Trimmed working set of {count} processes.";
            Log.Information("RAM trim completed: {Count} processes trimmed", count);
        }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  HIBERNATION TOGGLE
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty] private bool _isHibernationEnabled;

    [RelayCommand]
    private async Task ToggleHibernationAsync()
    {
        if (!Helpers.AdminHelper.IsElevated())
        {
            StatusMessage = "⚠ Toggling hibernation requires administrator privileges.";
            return;
        }

        var enabling = !IsHibernationEnabled;
        var action = enabling ? "Enable" : "Disable";
        var detail = enabling
            ? "This creates hiberfil.sys and allows the PC to hibernate."
            : "This deletes hiberfil.sys and frees disk space (often several GB).";

        var result = MessageBox.Show(
            $"{action} hibernation?\n\n{detail}",
            "Hibernation — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = $"{(enabling ? "Enabling" : "Disabling")} hibernation…";
        try
        {
            await _service.SetHibernationAsync(enabling);
            IsHibernationEnabled = PerformanceService.ReadHibernationEnabled();
            StatusMessage = $"✓ Hibernation {(enabling ? "enabled" : "disabled")}.";
            Log.Information("Hibernation {Action}", enabling ? "enabled" : "disabled");
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
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
            Log.Information("Performance settings restored to original snapshot");
            _snapshot = null;
            HasSnapshot = false;
            await RefreshAsync();
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (SecurityException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Error: {ex.Message}"; }
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

        // High Performance and Ultimate Performance plans force processor
        // min state to 100 %. The user cannot override this independently.
        var planKey = GetCurrentPlanKey();
        IsProcessorStateLocked = planKey is "high" or "ultimate";
    }
}
