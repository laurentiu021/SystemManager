// SysManager · DiskUsageEntry — model for disk space analysis
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A folder or drive with its total size and percentage of parent.
/// Used by the Disk Analyzer tab to show space breakdown.
/// </summary>
public sealed partial class DiskUsageEntry : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long _sizeBytes;
    [ObservableProperty] private double _percentage;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _folderCount;
    [ObservableProperty] private bool _isAccessDenied;

    /// <summary>Formatted size for display.</summary>
    public string SizeDisplay => CleanupCategory.HumanSize(SizeBytes);
}
