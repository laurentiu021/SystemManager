// SysManager · LogsArrival
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// System Logs opened on a log, a time range and severities: where the events a link counted are (#2887).
/// </summary>
/// <param name="Log">The log, one of the four System Logs lists: System, Application, Security or Setup.</param>
/// <param name="Within">
/// How far back the events go. System Logs picks the shortest of its time ranges that covers it.
/// </param>
/// <param name="Severities">The severities shown. The others are switched off.</param>
/// <remarks>
/// The Dashboard's Event Log alert and Recent Changes' problems line both sent the user to System Logs with nothing
/// but the tab, and System Logs starts on the System log over 24 hours with nothing loaded: an empty grid, and after
/// Refresh still not the events either had counted.
/// </remarks>
public sealed record LogsArrival(string Log, TimeSpan Within, IReadOnlyList<EventSeverity> Severities)
    : NavigationArrival
{
    /// <summary>
    /// What the Dashboard's Event Log alert counts: critical events in the System log over the last 7 days.
    /// </summary>
    public static LogsArrival CriticalSystemEvents { get; } =
        new("System", TimeSpan.FromDays(7), [EventSeverity.Critical]);

    /// <summary>
    /// What Recent Changes' problems line counts over its last <paramref name="days"/>: app crashes and programs that
    /// stopped responding, which Windows writes to the Application log as errors ("Application Error" 1000 and
    /// "Application Hang" 1002).
    /// </summary>
    public static LogsArrival AppProblems(int days) =>
        new("Application", TimeSpan.FromDays(days), [EventSeverity.Error]);
}
