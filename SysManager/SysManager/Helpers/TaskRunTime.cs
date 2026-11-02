// SysManager · TaskRunTime
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Management.Automation;

namespace SysManager.Helpers;

/// <summary>
/// A run time Task Scheduler reports for a task, read from a <c>Get-ScheduledTaskInfo</c> row, for Scheduled
/// Maintenance and Task Scheduler alike.
/// </summary>
/// <remarks>
/// For a task that has never run, Task Scheduler reports 30 November 1999 as the last run, with the result 267011
/// (the task has not run). Both tabs showed that date as a real run (#2748). No task can have run before the
/// Task Scheduler that reports it existed, so a time before 2000 is Task Scheduler's "never", not a time.
/// </remarks>
internal static class TaskRunTime
{
    /// <summary>The earliest time taken as a real run.</summary>
    internal static readonly DateTime Earliest = new(2000, 1, 1);

    /// <summary>
    /// The time in <paramref name="property"/> of <paramref name="row"/>, or null when it has none or holds Task
    /// Scheduler's "never".
    /// </summary>
    internal static DateTime? Read(PSObject? row, string property) =>
        Raw(row, property) is { } time && time >= Earliest ? time : null;

    /// <summary>Whether <paramref name="property"/> of <paramref name="row"/> says the task has never run.</summary>
    internal static bool IsNever(PSObject? row, string property) => Raw(row, property) is { } time && time < Earliest;

    /// <summary>A run time as both tabs show it: the time, "Never", or "—" when there is none to show.</summary>
    internal static string Display(DateTime? time, bool never) => never
        ? "Never"
        : time?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

    private static DateTime? Raw(PSObject? row, string property) => row?.Properties[property]?.Value switch
    {
        DateTime dt => dt,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
        _ => null,
    };
}
