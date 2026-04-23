// SysManager · DiskUsageEntry — model for disk space analysis
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A folder or drive with its total size and percentage of parent.
/// Used by the Disk Analyzer tab to show space breakdown.
/// </summary>
public partial class DiskUsageEntry : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private double _percentage;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _folderCount;
    [ObservableProperty] private bool _isAccessDenied;

    /// <summary>Formatted size for display.</summary>
    public string SizeDisplay => FormatSize(SizeBytes);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
