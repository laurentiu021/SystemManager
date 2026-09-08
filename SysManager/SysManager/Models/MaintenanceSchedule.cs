// SysManager · MaintenanceSchedule — a recurring maintenance task definition
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>How often the scheduled maintenance runs.</summary>
public enum MaintenanceFrequency
{
    Daily,
    Weekly,
}

/// <summary>Which maintenance action the schedule performs (maps to a safe CLI verb).</summary>
public enum MaintenanceAction
{
    /// <summary>Delete temporary files (CLI <c>--cleanup</c>).</summary>
    Cleanup,
    /// <summary>Purge the standby memory list (CLI <c>--purge-standby</c>).</summary>
    /// <remarks>
    /// Was <c>TrimRam</c>, which pointed at the wrong tab. Performance Mode's "Trim RAM" is
    /// <c>EmptyWorkingSet</c> per process and needs no elevation; this is
    /// <c>NtSetSystemInformation(MemoryPurgeStandbyList)</c> and needs administrator. Someone scheduling
    /// "TrimRam" reasonably expected the button of that name and got the other operation, with a
    /// different elevation requirement — so the name also misled about whether the task would work
    /// unelevated (#1524). Renaming this value is safe because a schedule is never persisted by name: it
    /// is built fresh from the view model and turned into a Windows scheduled task. The CLI verb is the
    /// part that crosses a persistence boundary, which is why <c>--trim-ram</c> is still accepted.
    /// </remarks>
    PurgeStandby,
}

/// <summary>
/// A user-defined recurring maintenance schedule. The watchdog registers a single Windows
/// scheduled task from this definition that launches SysManager headless with the matching
/// CLI verb. Only the fields here are configurable — the command itself is built from a
/// fixed whitelist, so no free-form text ever reaches the scheduler.
/// </summary>
public sealed record MaintenanceSchedule(
    MaintenanceAction Action,
    MaintenanceFrequency Frequency,
    int Hour,
    int Minute,
    // Day of week for weekly schedules (ignored for daily). Sunday = 0.
    DayOfWeek DayOfWeek = DayOfWeek.Sunday)
{
    /// <summary>The CLI argument string this schedule runs (whitelisted, no user text).</summary>
    public string CliArguments => Action switch
    {
        MaintenanceAction.Cleanup => "--cleanup --silent",
        MaintenanceAction.PurgeStandby => "--purge-standby --silent",
        _ => "--help",
    };

    /// <summary>
    /// The plain-language name of an action, for anything that shows one to the user.
    /// </summary>
    /// <remarks>
    /// Static because the action picker needs a label for an enum value it has no schedule for. It used
    /// to exist only as an instance property, so the picker fell back to the enum's own
    /// <c>ToString()</c> — the dropdown offered "Cleanup" and "TrimRam" while the confirmation dialog
    /// that followed said "Purge standby memory", two names for one thing in consecutive steps (#1524).
    /// </remarks>
    public static string LabelFor(MaintenanceAction action) => action switch
    {
        MaintenanceAction.Cleanup => "Clean temporary files",
        MaintenanceAction.PurgeStandby => "Purge standby memory",
        _ => "Unknown",
    };

    public string ActionLabel => LabelFor(Action);

    /// <summary>A plain-language summary of when this runs (e.g. "Every Sunday at 03:00").</summary>
    public string Summary => Frequency == MaintenanceFrequency.Daily
        ? $"Every day at {Hour:D2}:{Minute:D2}"
        : $"Every {DayOfWeek} at {Hour:D2}:{Minute:D2}";
}

/// <summary>The live state of the registered maintenance task, read back from Windows.</summary>
public sealed record MaintenanceStatus(
    bool Exists,
    string? State,
    DateTime? LastRun,
    DateTime? NextRun,
    string? LastResultDescription);
