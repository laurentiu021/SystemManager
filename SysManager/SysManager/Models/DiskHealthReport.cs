// SysManager · DiskHealthReport
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Friendly, per-disk health report derived from Windows Storage reliability
/// counters (SMART-sourced). All fields nullable because not every driver
/// exposes every counter.
/// </summary>
public partial class DiskHealthReport : ObservableObject
{
    [ObservableProperty] private string _friendlyName = "";
    [ObservableProperty] private string _mediaType = "";       // HDD / SSD / NVMe
    [ObservableProperty] private string _busType = "";
    [ObservableProperty] private double _sizeGB;
    [ObservableProperty] private string _healthStatus = "";    // Healthy / Warning / Unhealthy
    [ObservableProperty] private double? _temperatureC;
    [ObservableProperty] private double? _temperatureMaxC;
    [ObservableProperty] private int? _wearPercent;            // 0 = new, 100 = worn out (SSD only)
    [ObservableProperty] private long? _powerOnHours;
    [ObservableProperty] private long? _readErrors;
    [ObservableProperty] private long? _writeErrors;
    [ObservableProperty] private long? _startStopCount;
    [ObservableProperty] private string _verdict = "";         // plain-English summary
    [ObservableProperty] private string _verdictColorHex = "#9AA0A6";
}
