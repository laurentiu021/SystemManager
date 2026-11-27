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

    // Rolled-up metrics for the status pills, each null until something measured it. They were 0 then, which the
    // pills showed as 0 ms and 0.0%, the best readings there are, while every ping timed out (#3385).

    /// <summary>The worst loss of the targets that were pinged, or null before the first ping.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorstLossDisplay))]
    private double? _worstLossPercent;

    /// <summary>
    /// The worst jitter of the targets that have one, or null while none has the two replies it needs.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorstJitterDisplay))]
    private double? _worstJitterMs;

    /// <summary>The average of the targets' average pings, or null while no target got a reply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AveragePingDisplay))]
    private double? _averagePingMs;

    /// <summary>The average ping as the AVG PING pill shows it, or "—" while no target got a reply.</summary>
    public string AveragePingDisplay => Shown(AveragePingMs, "F0", " ms");

    /// <summary>The worst loss as the WORST LOSS pill shows it, or "—" before the first ping.</summary>
    public string WorstLossDisplay => Shown(WorstLossPercent, "F1", "%");

    /// <summary>The worst jitter as the WORST JITTER pill shows it, or "—" while no target has one.</summary>
    public string WorstJitterDisplay => Shown(WorstJitterMs, "F0", " ms");

    /// <summary>
    /// <paramref name="value"/> in <paramref name="format"/> followed by <paramref name="unit"/>, or "—" for none.
    /// </summary>
    private static string Shown(double? value, string format, string unit) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) + unit : "—";
}
