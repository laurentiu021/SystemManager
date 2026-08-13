// SysManager · PerformanceProfile — model for performance mode settings
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents the current performance profile state read from the system.
/// Every property is read-only from the system — the ViewModel holds the
/// "desired" state separately so we can diff before applying.
/// </summary>
public sealed partial class PerformanceProfile : ObservableObject
{
    // ── Power plan ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileSummary))]
    private string _activePlanName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileSummary))]
    private string _activePlanGuid = "";

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
    /// <remarks>
    /// GUID first, name last. The plan's display name is user-editable and localized, so matching on
    /// it decided two things wrongly: a custom plan called e.g. "Ultimate Battery Saver" — copied from
    /// Balanced, and carrying Balanced's GUID — was reported as "Ultimate Performance", and on
    /// non-English Windows the real Ultimate plan ("Ultimative Leistung", "Rendimiento máximo") was
    /// never recognised at all. The name check now only catches a DUPLICATE of the Ultimate scheme,
    /// which gets a fresh random GUID but keeps the name, and it runs after every GUID has been ruled
    /// out.
    /// </remarks>
    public string ProfileSummary => PlanLabel() ?? ActivePlanName;

    private string? PlanLabel()
    {
        if (MatchesPlan(PowerPlans.UltimatePerformance)) return "Ultimate Performance";
        if (MatchesPlan(PowerPlans.HighPerformance)) return "High Performance";
        if (MatchesPlan(PowerPlans.Balanced)) return "Balanced";
        // Only reached when the GUID is none of the stock three: a duplicated Ultimate scheme.
        return ActivePlanName.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)
            ? "Ultimate Performance"
            : null;
    }

    private bool MatchesPlan(string planGuid) =>
        ActivePlanGuid.Contains(planGuid, StringComparison.OrdinalIgnoreCase);
}
