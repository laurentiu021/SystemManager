// SysManager · PingTarget
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A ping target tracked by the network monitor. Each target owns its color
/// (assigned when added) and a live latency series. Enabled can be toggled
/// at runtime to temporarily hide a series without losing its history.
/// </summary>
public sealed partial class PingTarget : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _isCustom;       // user-added, can be removed
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastLatencyDisplay))]
    private double? _lastLatencyMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AverageDisplay))]
    private double? _averageMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JitterDisplay))]
    private double? _jitterMs;    // stddev of recent samples

    [ObservableProperty] private double _lossPercent;
    [ObservableProperty] private string _status = "—"; // "OK" / "Timeout" / "Error"
    [ObservableProperty] private string _colorHex = "#4CC9F0";
    [ObservableProperty] private TargetRole _role = TargetRole.Generic;

    /// <summary>The last reply's latency as the row shows it, or "—" when the last ping got none.</summary>
    /// <remarks>
    /// The row bound the three figures through a <c>StringFormat</c> with a <c>FallbackValue</c> of "—". A fallback is
    /// for a binding that fails, not for a value that is null, so a ping that timed out read a bare " ms", and an
    /// average or jitter not yet measured read nothing at all.
    /// </remarks>
    public string LastLatencyDisplay => LastLatencyMs is { } ms
        ? string.Create(CultureInfo.InvariantCulture, $"{ms:F0} ms")
        : "—";

    /// <summary>The average latency as the row shows it, or "—" before there is one.</summary>
    public string AverageDisplay => AverageMs is { } ms ? ms.ToString("F1", CultureInfo.InvariantCulture) : "—";

    /// <summary>The jitter as the row shows it, or "—" before there is one.</summary>
    public string JitterDisplay => JitterMs is { } ms ? ms.ToString("F0", CultureInfo.InvariantCulture) : "—";

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
