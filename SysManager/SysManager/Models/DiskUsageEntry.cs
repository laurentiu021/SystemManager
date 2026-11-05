// SysManager · DiskUsageEntry — model for disk space analysis
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

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
    public string SizeDisplay => FormatHelper.FormatSize(SizeBytes);

    /// <summary>
    /// What the folder list's row shows, as a screen reader says it: "AppData, 38.0 GB, 1,204 files, 61 folders". The
    /// class's own was its type name (#2785). Invariant, as the row's own figures are.
    /// </summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Name}, {SizeDisplay}, {FileCount:N0} files, {FolderCount:N0} folders")
        + (IsAccessDenied ? ", partly unreadable" : "");
}
