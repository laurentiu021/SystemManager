// SysManager · HealthDiagnostic
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

public enum HealthVerdict
{
    Good,               // everything clean
    LocalNetwork,       // loss/jitter on the local gateway
    IspOrUpstream,      // loss/jitter on public DNS but not gateway
    GameServer,         // only the game target(s) are bad
    StreamingService,   // only streaming targets bad
    Mixed,              // multiple layers impacted
    Unknown             // not enough data yet
}

/// <summary>
/// Aggregated health view over the last few seconds of ping data.
/// Updated in place by the analyzer so a single binding covers the UI.
/// </summary>
public sealed partial class HealthDiagnostic : ObservableObject
{
    [ObservableProperty] private HealthVerdict _verdict = HealthVerdict.Unknown;
    [ObservableProperty] private string _headline = "Waiting for data…";
    [ObservableProperty] private string _detail = "";
    // A theme key, not a literal — see StatusColors. This is the "waiting for data" state, so it
    // must be as theme-aware as every value the analyzer later assigns.
    [ObservableProperty] private string _colorHex = StatusColors.Neutral;

    // Rolled-up metrics for the status pills.
    [ObservableProperty] private double _worstLossPercent;
    [ObservableProperty] private double _worstJitterMs;
    [ObservableProperty] private double _averagePingMs;
}
