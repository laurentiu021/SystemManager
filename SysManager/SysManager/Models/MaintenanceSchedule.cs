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
/// <remarks>
/// Every action has to work without administrator rights, because the task runs at the limited run level. The
/// standby purge was offered too, and it needs administrator, so every scheduled run of it failed. It was taken
/// out rather than registered elevated: the exe sits in a folder its user can write to, so an elevated task would
/// start whatever replaced it with administrator rights (#2593). A schedule saved while it was offered still runs,
/// and <see cref="MaintenanceStatus.PurgesStandby"/> is how the tab recognises one. The CLI keeps
/// <c>--purge-standby</c> for scripts run as administrator.
/// <para>Removing the value is safe because a schedule is never persisted by name: it is built fresh from the view
/// model and turned into a Windows scheduled task, and the task stores the CLI arguments.</para>
/// </remarks>
public enum MaintenanceAction
{
    /// <summary>Delete temporary files (CLI <c>--cleanup</c>).</summary>
    Cleanup,
}

/// <summary>
/// A user-defined recurring maintenance schedule. <c>MaintenanceSchedulerService</c> registers a single
/// Windows scheduled task from this definition that launches SysManager headless with the matching
/// CLI verb. Only the fields here are configurable — the command itself is built from a
/// fixed allowlist, so no free-form text ever reaches the scheduler.
/// </summary>
public sealed record MaintenanceSchedule(
    MaintenanceAction Action,
    MaintenanceFrequency Frequency,
    int Hour,
    int Minute,
    // Day of week for weekly schedules (ignored for daily). Sunday = 0.
    DayOfWeek DayOfWeek = DayOfWeek.Sunday,
    bool RunOnBattery = true,
    bool OnlyWhenIdle = false)
{
    /// <summary>The CLI argument string this schedule runs (allowlisted, no user text).</summary>
    public string CliArguments => Action switch
    {
        MaintenanceAction.Cleanup => "--cleanup --silent",
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
        _ => "Unknown",
    };

    public string ActionLabel => LabelFor(Action);

    /// <summary>
    /// A plain-language summary of when this runs, including the conditions that can stop it
    /// (e.g. "Every Sunday at 03:00, only while plugged in and only when you are not using the PC").
    /// </summary>
    /// <remarks>
    /// The conditions belong in the sentence because they decide whether the schedule happens at all, and
    /// the tab used to promise the time unconditionally while Windows quietly applied a policy nobody had
    /// been shown (#1578). A summary that states the time and hides the conditions is the part that made
    /// "it says next run 03:00 but it never ran" possible.
    /// </remarks>
    public string Summary
    {
        get
        {
            var when = Frequency == MaintenanceFrequency.Daily
                ? $"Every day at {Hour:D2}:{Minute:D2}"
                : $"Every {DayOfWeek} at {Hour:D2}:{Minute:D2}";

            // Only the RESTRICTIONS are named. "Even on battery" is the default and adds nothing a user
            // needs to plan around; "only while plugged in" is the one that explains a run that never came.
            List<string> conditions = [];
            if (!RunOnBattery) conditions.Add("only while plugged in");
            if (OnlyWhenIdle) conditions.Add("only when you are not using the PC");

            return conditions.Count == 0 ? when : $"{when}, {string.Join(" and ", conditions)}";
        }
    }
}

/// <summary>The live state of the registered maintenance task, read back from Windows.</summary>
public sealed record MaintenanceStatus(
    bool Exists,
    string? State,
    DateTime? LastRun,
    DateTime? NextRun,
    string? LastResultDescription,
    // How many scheduled runs Windows recorded as missed.
    int? MissedRuns = null,
    // Whether the task's command line purges standby memory, which needs administrator rights the task lacks.
    bool PurgesStandby = false)
{
    /// <summary>No task is registered: Windows answered that the task does not exist.</summary>
    /// <remarks>
    /// A named value rather than five nulls at each call site. Adding a field to this record previously
    /// meant editing each of them, and a missed one is a compile error only because every field happens to
    /// be nullable.
    /// <para>Not what a failed read returns. It used to be, so a read that failed told the user nothing was
    /// scheduled (#2487); <c>MaintenanceSchedulerService.GetStatusAsync</c> now returns null for that.</para>
    /// </remarks>
    public static MaintenanceStatus NotRegistered { get; } = new(false, null, null, null, null);

    /// <summary>
    /// How Windows says the schedule is not firing, or null when there is nothing to report.
    /// </summary>
    /// <remarks>
    /// There is no <c>LastTaskResult</c> code for "skipped because the conditions were not met" — Windows
    /// expresses that by simply not running, which is exactly why a stale Last run and a confident Next run
    /// were all the user got (#1578). <c>NumberOfMissedRuns</c> is the signal that does exist, so this is the
    /// honest version of the explanation rather than an invented result code.
    /// </remarks>
    public string? MissedRunsWarning => MissedRuns is > 0
        ? $"{MissedRuns} scheduled run{(MissedRuns == 1 ? "" : "s")} did not happen — the PC was probably "
          + "off, asleep, or blocked by one of the conditions below."
        : null;

    /// <summary>
    /// What to tell someone whose schedule purges standby memory, or null when it does not.
    /// </summary>
    /// <remarks>
    /// The task runs without administrator rights and a purge needs them, so every run of such a schedule fails.
    /// The purge is no longer offered, but a schedule saved while it was keeps running, and its only trace was
    /// LAST RESULT reading "Last run returned 0x00000001" (#2593). Nothing is removed on the user's behalf: this
    /// says what Save and Remove will do, and where a purge runs with the rights it needs.
    /// </remarks>
    public string? StandbyPurgeWarning => PurgesStandby
        ? "This schedule purges standby memory, which needs administrator rights the scheduled task does not "
          + "have, so every run fails. Saving a schedule below replaces it with a temporary-file cleanup, and "
          + "Remove schedule removes it. To purge standby memory automatically, tick \"Automatically purge when "
          + "available RAM is low\" in Standby List Cleaner while SysManager runs as administrator."
        : null;
}
