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

    public string FullPath => FullPathOf(Path, Name);

    /// <summary>
    /// A task's folder, as Task Scheduler reports it with its closing backslash, joined to its name: what the
    /// telemetry list names a single task by (#2781).
    /// </summary>
    public static string FullPathOf(string folder, string name) => $"{folder}{name}";

    /// <summary>Whether Task Scheduler says the task has never run, which it reports as a date in 1999.</summary>
    public bool HasNeverRun { get; init; }

    public string LastRunDisplay => Helpers.TaskRunTime.Display(LastRun, HasNeverRun);
    public string NextRunDisplay => Helpers.TaskRunTime.Display(NextRun, never: false);

    /// <summary>
    /// Who registered the task, phrased for a tooltip. Many tasks report no author at all, and a
    /// tooltip bound straight to <see cref="Author"/> would then render as an empty box on hover —
    /// worse than none, because it looks like something failed to load.
    /// </summary>
    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author)
        ? "No publisher recorded for this task."
        : $"Created by: {Author}";

    /// <summary>
    /// What the task is for, phrased for a cell. A great many Windows tasks carry no description at all,
    /// and the raw value rendered as an empty cell — which reads as data that failed to load rather than
    /// data Windows never had. Says so instead.
    /// </summary>
    public string DescriptionDisplay => string.IsNullOrWhiteSpace(Description)
        ? "No description provided by Windows."
        : Description;
}
