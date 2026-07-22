// SysManager · ProcessNetworkUsage
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// One row in the Bandwidth Monitor's top-consumers list: a process and its current network
/// footprint. The always-available signals (connection count, remote-endpoint summary) come
/// from the no-admin connection source; the precise per-process rates and session totals are
/// populated only in the elevated ETW mode and are otherwise left at zero and hidden in the UI.
/// </summary>
public sealed partial class ProcessNetworkUsage : ObservableObject
{
    /// <summary>Owning process id. Also the stable identity used to reconcile rows in place.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Executable name (e.g. "chrome.exe"), or a placeholder when it can't be resolved.</summary>
    public required string ProcessName { get; init; }

    /// <summary>Per-app icon when available (from the shared icon service); may be null.</summary>
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>Number of active TCP/UDP connections owned by this process.</summary>
    [ObservableProperty] private int _connectionCount;

    /// <summary>Short human summary of the remote endpoints (e.g. "443, 443, 27015"), for context.</summary>
    [ObservableProperty] private string _remoteSummary = "";

    /// <summary>True while the process has at least one active connection (drives the "active" dot).</summary>
    public bool IsActive => ConnectionCount > 0;

    // ── ETW-only precise figures (zero / hidden in the no-admin connection mode) ──

    /// <summary>Precise download rate in bytes/sec (ETW mode only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownDisplay))]
    private double _downBytesPerSec;

    /// <summary>Precise upload rate in bytes/sec (ETW mode only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpDisplay))]
    private double _upBytesPerSec;

    /// <summary>Total bytes downloaded by this process since monitoring started (ETW mode only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDisplay))]
    private long _totalDownBytes;

    /// <summary>Total bytes uploaded by this process since monitoring started (ETW mode only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDisplay))]
    private long _totalUpBytes;

    /// <summary>Human-readable download rate for the grid (e.g. "8.2 Mbps").</summary>
    public string DownDisplay => FormatHelper.FormatRate(DownBytesPerSec);

    /// <summary>Human-readable upload rate for the grid.</summary>
    public string UpDisplay => FormatHelper.FormatRate(UpBytesPerSec);

    /// <summary>Combined session data volume (down + up) for the grid, e.g. "1.4 GB".</summary>
    public string TotalDisplay => FormatHelper.FormatSize(TotalDownBytes + TotalUpBytes);

    partial void OnConnectionCountChanged(int value) => OnPropertyChanged(nameof(IsActive));
}
