// SysManager · PingTarget
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A ping target tracked by the network monitor. Each target owns its color
/// (assigned when added) and a live latency series. Enabled can be toggled
/// at runtime to temporarily hide a series without losing its history.
/// </summary>
public partial class PingTarget : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _isCustom;       // user-added, can be removed
    [ObservableProperty] private double? _lastLatencyMs;
    [ObservableProperty] private double? _averageMs;
    [ObservableProperty] private double? _jitterMs;    // stddev of recent samples
    [ObservableProperty] private double _lossPercent;
    [ObservableProperty] private string _status = "—"; // "OK" / "Timeout" / "Error"
    [ObservableProperty] private string _colorHex = "#4CC9F0";
    [ObservableProperty] private TargetRole _role = TargetRole.Generic;

    public PingTarget() { }

    public PingTarget(string name, string host, string colorHex, bool isCustom = false, TargetRole role = TargetRole.Generic)
    {
        _name = name;
        _host = host;
        _colorHex = colorHex;
        _isCustom = isCustom;
        _role = role;
    }
}
