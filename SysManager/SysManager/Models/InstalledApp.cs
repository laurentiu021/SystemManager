// SysManager · InstalledApp — model for installed applications
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// An installed Windows application as reported by winget list.
/// </summary>
public partial class InstalledApp : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>Formatted size for display.</summary>
    public string SizeDisplay => SizeBytes > 0 ? FormatSize(SizeBytes) : "—";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
