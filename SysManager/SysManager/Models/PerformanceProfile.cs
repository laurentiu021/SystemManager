// SysManager · PerformanceProfile — model for performance mode settings
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents the current performance profile state read from the system.
/// Every property is read-only from the system — the ViewModel holds the
/// "desired" state separately so we can diff before applying.
/// </summary>
public partial class PerformanceProfile : ObservableObject
{
    // ── Power plan ──
    [ObservableProperty] private string _activePlanName = "";
    [ObservableProperty] private string _activePlanGuid = "";

    // ── Visual effects ──
    [ObservableProperty] private bool _visualEffectsReduced;

    // ── Game Mode ──
    [ObservableProperty] private bool _gameModeEnabled;

    // ── Xbox Game Bar / DVR overlay ──
    [ObservableProperty] private bool _xboxGameBarDisabled;

    // ── NVIDIA GPU ──
    [ObservableProperty] private bool _gpuMaxPerformance;
    [ObservableProperty] private bool _hasNvidiaGpu;
    [ObservableProperty] private string _nvidiaGpuName = "";

    // ── Processor state ──
    [ObservableProperty] private bool _processorMaxState;
    [ObservableProperty] private int _processorMinPercent;

    /// <summary>Friendly summary of the active profile.</summary>
    public string ProfileSummary
    {
        get
        {
            if (ActivePlanName.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                return "Ultimate Performance";
            if (ActivePlanGuid.Contains("8c5e7fda", StringComparison.OrdinalIgnoreCase))
                return "High Performance";
            if (ActivePlanGuid.Contains("381b4222", StringComparison.OrdinalIgnoreCase))
                return "Balanced";
            return ActivePlanName;
        }
    }
}
