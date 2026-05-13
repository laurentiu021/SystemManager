// SysManager · ProcessEntry — model for running processes
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryDisplay))]
    private long _memoryBytes;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private DateTime _startTime;
    [ObservableProperty] private int _threadCount;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _hasMainWindow;
    [ObservableProperty] private string _plainDescription = "";
    [ObservableProperty] private string _category = "Unknown";
    [ObservableProperty] private string _safetyLevel = "Unknown";

    /// <summary>True when the process has a valid, accessible file path (cached on creation).</summary>
    [ObservableProperty] private bool _canOpenFileLocation;

    /// <summary>Formatted memory for display.</summary>
    public string MemoryDisplay => CleanupCategory.HumanSize(MemoryBytes);
}
