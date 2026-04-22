// SysManager · TracerouteHop
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A single hop in a traceroute result. LatencyMs is an average across the
/// attempted probes (null if all probes timed out at this hop).
/// </summary>
public partial class TracerouteHop : ObservableObject
{
    [ObservableProperty] private int _hopNumber;
    [ObservableProperty] private string _address = "*";
    [ObservableProperty] private string _hostName = "";
    [ObservableProperty] private double? _latencyMs;
    [ObservableProperty] private string _status = "";
}
