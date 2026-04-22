// SysManager · HealthDiagnostic
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

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
public partial class HealthDiagnostic : ObservableObject
{
    [ObservableProperty] private HealthVerdict _verdict = HealthVerdict.Unknown;
    [ObservableProperty] private string _headline = "Waiting for data…";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _colorHex = "#9AA0A6";

    // Rolled-up metrics for the status pills.
    [ObservableProperty] private double _worstLossPercent;
    [ObservableProperty] private double _worstJitterMs;
    [ObservableProperty] private double _averagePingMs;
}
