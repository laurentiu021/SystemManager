// SysManager · FriendlyEventEntry
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A single Windows event-log entry, normalized into a friendlier shape.
/// Wraps the essentials plus an optional plain-English explanation
/// attached by <see cref="Services.EventExplainer"/>.
/// </summary>
public partial class FriendlyEventEntry : ObservableObject
{
    [ObservableProperty] private DateTime _timestamp;
    [ObservableProperty] private string _logName = "";          // System / Application / Security / Setup
    [ObservableProperty] private string _providerName = "";      // source
    [ObservableProperty] private int _eventId;
    [ObservableProperty] private EventSeverity _severity;
    [ObservableProperty] private string _severityLabel = "";
    [ObservableProperty] private string _message = "";           // first line / summary
    [ObservableProperty] private string _fullMessage = "";       // full rendered text
    [ObservableProperty] private string _xml = "";               // raw xml for power users
    [ObservableProperty] private string? _machineName;
    [ObservableProperty] private string? _userName;
    [ObservableProperty] private long _recordId;
    [ObservableProperty] private string _explanation = "";       // friendly explanation
    [ObservableProperty] private string _recommendation = "";    // what to try

    public string SeverityIcon => Severity switch
    {
        EventSeverity.Critical => "⛔",
        EventSeverity.Error    => "🔴",
        EventSeverity.Warning  => "🟡",
        EventSeverity.Info     => "🔵",
        EventSeverity.Verbose  => "⚪",
        _ => "•"
    };

    public string SeverityColor => Severity switch
    {
        EventSeverity.Critical => "#FF3B30",
        EventSeverity.Error    => "#FF6B6B",
        EventSeverity.Warning  => "#FFD166",
        EventSeverity.Info     => "#4CC9F0",
        EventSeverity.Verbose  => "#9AA0A6",
        _ => "#9AA0A6"
    };
}

public enum EventSeverity
{
    Verbose = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}
