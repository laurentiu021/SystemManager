// SysManager · ScheduledTaskInfo
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>Safety classification for a scheduled task — drives UI color and confirmation.</summary>
public enum TaskCategory
{
    /// <summary>User/vendor task — safe to toggle.</summary>
    ThirdParty,

    /// <summary>Well-known telemetry/CEIP task — commonly disabled by debloat tools.</summary>
    Telemetry,

    /// <summary>Windows system task — may be load-bearing; confirm before disabling.</summary>
    System,
}

/// <summary>
/// A Windows scheduled task projected for the UI. <see cref="LastRun"/>/<see cref="NextRun"/>
/// are null until lazily loaded on selection (a separate per-task query).
/// </summary>
public sealed record ScheduledTaskInfo(
    string Name,
    string Path,
    string State,
    string? Author,
    string? Description,
    TaskCategory Category,
    DateTime? LastRun,
    DateTime? NextRun)
{
    public bool IsEnabled => !string.Equals(State, "Disabled", StringComparison.OrdinalIgnoreCase);
    public bool IsSystem => Category == TaskCategory.System;

    public string CategoryLabel => Category switch
    {
        TaskCategory.Telemetry => "Telemetry",
        TaskCategory.System => "System",
        _ => "Third-party",
    };

    public string FullPath => $"{Path}{Name}";
    public string LastRunDisplay => LastRun is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "—";
    public string NextRunDisplay => NextRun is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "—";
}
