// SysManager · TracerouteHop
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A single hop in a traceroute result. LatencyMs is an average across the
/// attempted probes (null if all probes timed out at this hop).
/// </summary>
public sealed partial class TracerouteHop : ObservableObject
{
    /// <summary>What a screen reader calls the grid row, rather than the class's type name (#2785).</summary>
    public override string ToString() => $"Hop {HopNumber}, {Address}";

    [ObservableProperty] private int _hopNumber;
    [ObservableProperty] private string _address = "*";
    [ObservableProperty] private double? _latencyMs;
    [ObservableProperty] private string _status = "";
}
