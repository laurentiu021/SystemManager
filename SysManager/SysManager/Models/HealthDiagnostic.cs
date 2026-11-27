// SysManager · HealthDiagnostic
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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
    RemoteTarget,       // only a plain target is bad: google.com, or one the user added
    Inconclusive,       // a far end is bad, and no public DNS reference was measured to tell where
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorstLossDisplay))]
    private double _worstLossPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorstJitterDisplay))]
    private double _worstJitterMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AveragePingDisplay))]
    private double _averagePingMs;

    /// <summary>The average ping as the AVG PING pill shows it.</summary>
    public string AveragePingDisplay => string.Create(CultureInfo.InvariantCulture, $"{AveragePingMs:F0} ms");

    /// <summary>The worst loss as the WORST LOSS pill shows it.</summary>
    public string WorstLossDisplay => string.Create(CultureInfo.InvariantCulture, $"{WorstLossPercent:F1}%");

    /// <summary>The worst jitter as the WORST JITTER pill shows it.</summary>
    public string WorstJitterDisplay => string.Create(CultureInfo.InvariantCulture, $"{WorstJitterMs:F0} ms");
}
