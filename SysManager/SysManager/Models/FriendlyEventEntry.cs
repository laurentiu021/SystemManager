// SysManager · FriendlyEventEntry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// A single Windows event-log entry, normalized into a friendlier shape.
/// Wraps the essentials plus an optional plain-English explanation
/// attached by <see cref="Services.EventExplainer"/>.
/// </summary>
public sealed partial class FriendlyEventEntry : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RelativeTime))]
    [NotifyPropertyChangedFor(nameof(FullTimestamp))]
    private DateTime _timestamp;
    [ObservableProperty] private string _logName = "";          // System / Application / Security / Setup
    [ObservableProperty] private string _providerName = "";      // source
    [ObservableProperty] private int _eventId;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeverityIcon))]
    [NotifyPropertyChangedFor(nameof(SeverityColor))]
    private EventSeverity _severity;
    [ObservableProperty] private string _severityLabel = "";
    [ObservableProperty] private string _message = "";           // first line / summary
    [ObservableProperty] private string _fullMessage = "";       // full rendered text
    [ObservableProperty] private string _xml = "";               // raw xml for power users
    [ObservableProperty] private string? _machineName;
    [ObservableProperty] private string? _userName;
    [ObservableProperty] private long _recordId;
    [ObservableProperty] private string _explanation = "";       // friendly explanation
    [ObservableProperty] private string _recommendation = "";    // what to try
    [ObservableProperty] private bool _isHighlighted;

    public string SeverityIcon => Severity switch
    {
        EventSeverity.Critical => "⛔",
        EventSeverity.Error => "🔴",
        EventSeverity.Warning => "🟡",
        EventSeverity.Info => "🔵",
        EventSeverity.Verbose => "⚪",
        _ => "•"
    };

    /// <summary>
    /// Theme resource key for the severity dot/text in the log list, resolved by
    /// <c>HexToBrushConverter</c>. Keys rather than literals for the same reason as
    /// <see cref="Helpers.StatusColors"/>: these were dark-calibrated hex constants that
    /// <c>ThemeService</c> could not recompute, so on a light preset the severity colour rendered
    /// pale on a near-white row. Critical keeps its own key because the log list gives it a distinct
    /// treatment from a plain error.
    /// </summary>
    public string SeverityColor => Severity switch
    {
        EventSeverity.Critical => "CriticalText",
        EventSeverity.Error => StatusColors.Bad,
        EventSeverity.Warning => StatusColors.Warning,
        EventSeverity.Info => StatusColors.Info,
        EventSeverity.Verbose => StatusColors.Neutral,
        _ => StatusColors.Neutral
    };

    /// <summary>
    /// Human-friendly relative timestamp, e.g. "2 min ago", "3 hours ago".
    /// </summary>
    public string RelativeTime => FormatRelative(Timestamp);

    /// <summary>
    /// Full timestamp for tooltip display.
    /// </summary>
    public string FullTimestamp => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatRelative(DateTime ts)
    {
        if (ts == DateTime.MinValue) return "—";
        var span = DateTime.Now - ts;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}w ago";
        return ts.ToString("yyyy-MM-dd");
    }
}

public enum EventSeverity
{
    Verbose = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}
