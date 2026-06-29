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
    /// <summary>Purge the standby memory list (CLI <c>--trim-ram</c>).</summary>
    TrimRam,
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
        MaintenanceAction.TrimRam => "--trim-ram --silent",
        _ => "--help",
    };

    public string ActionLabel => Action switch
    {
        MaintenanceAction.Cleanup => "Clean temporary files",
        MaintenanceAction.TrimRam => "Purge standby memory",
        _ => "Unknown",
    };

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
