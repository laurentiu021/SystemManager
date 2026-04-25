// SysManager · ProcessEntry — model for running processes
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A running Windows process with its resource usage.
/// </summary>
public partial class ProcessEntry : ObservableObject
{
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private long _memoryBytes;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private DateTime _startTime;
    [ObservableProperty] private int _threadCount;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>Formatted memory for display.</summary>
    public string MemoryDisplay => FormatSize(MemoryBytes);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
