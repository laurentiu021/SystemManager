// SysManager · UndoChange
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Which of SysManager's kept copies a change was found in (#1525). Each is the copy one tab keeps so that it can put
/// its own change back, and naming the copy names the tab.
/// </summary>
public enum UndoChangeKind
{
    /// <summary>Performance Mode's record of the settings from before it was first used.</summary>
    PerformanceMode,

    /// <summary>The Services tab's record of how each service it turned off used to start.</summary>
    Services,

    /// <summary>The copy of the hosts file kept beside it, from before SysManager first changed it.</summary>
    HostsFile,

    /// <summary>The copy of the environment variables kept from before SysManager first changed them.</summary>
    EnvironmentVariables,

    /// <summary>Game mode, while it is on or after it was left on by a run that did not end cleanly.</summary>
    GamingProfile,

    /// <summary>The settings saved in Settings Watchdog that have changed since.</summary>
    SettingsWatchdog,
}

/// <summary>One change Undo Changes can put back, worded for its row and for the question asked before it.</summary>
/// <param name="Kind">The kept copy it was found in.</param>
/// <param name="Title">The tab that made it, as the sidebar names that tab.</param>
/// <param name="Detail">What the change was.</param>
/// <param name="Caption">When it happened, or how it goes back; empty when there is nothing to say.</param>
/// <param name="NeedsAdmin">True when putting it back needs administrator rights.</param>
/// <param name="ActionText">What the row's button says.</param>
/// <param name="Lines">One line for each thing putting it back changes, as the question lists them.</param>
/// <param name="Question">The question asked before anything changes; empty for a row that only opens a tab.</param>
/// <param name="QuestionTitle">The title of the window that asks it.</param>
/// <param name="OpensTab">The tab the button opens instead of putting anything back, or null.</param>
public sealed record UndoChange(
    UndoChangeKind Kind,
    string Title,
    string Detail,
    string Caption,
    bool NeedsAdmin,
    string ActionText,
    IReadOnlyList<string> Lines,
    string Question,
    string QuestionTitle,
    string? OpensTab = null)
{
    /// <summary>The row in words, which is what a screen reader announces for it.</summary>
    /// <remarks>The caption ends a sentence of its own, so whatever is read after it does not run on from it.</remarks>
    public override string ToString() => Caption.Length == 0 ? $"{Title}. {Detail}" : $"{Title}. {Detail} {Caption}.";
}

/// <summary>Why Undo Changes could not use a kept copy.</summary>
public enum UndoProblemKind
{
    /// <summary>The copy is there and could not be read just now.</summary>
    Unreadable,

    /// <summary>The copy was read and is not one that can be put back, which looking again will not change.</summary>
    Damaged,

    /// <summary>
    /// What is on the PC now could not be read, so there was nothing to compare the copy with — or, for the hosts
    /// file and the environment variables, one of the two could not be read.
    /// </summary>
    CannotCompare,

    /// <summary>
    /// The copy is there and could not be read or used, without saying which — or a newer SysManager wrote it. Settings
    /// Watchdog's saved settings and game mode's record of a session are the two read that way.
    /// </summary>
    Unusable,
}

/// <summary>A kept copy Undo Changes could not use when it last looked.</summary>
/// <param name="Kind">The copy.</param>
/// <param name="Why">Why it could not be used.</param>
public sealed record UndoProblem(UndoChangeKind Kind, UndoProblemKind Why);

/// <summary>Everything Undo Changes found in the kept copies when it last looked.</summary>
/// <param name="Changes">
/// What can be put back, in one fixed order: Performance Mode, Services, the hosts file, the environment variables,
/// Gaming Profile, then Settings Watchdog.
/// </param>
/// <param name="Problems">The kept copies that could not be used, and why, instead of being described as nothing.</param>
/// <param name="PerformanceWaitsForGameMode">
/// True when Performance Mode has a record while game mode is on, was left on, or might have been — its own record
/// could not be read. The settings are game mode's until it is off, so Performance Mode is listed only once it is.
/// </param>
/// <param name="GameModeNotKnown">
/// True when Performance Mode waits only because game mode's record could not be read: game mode may well be off.
/// </param>
public sealed record UndoScan(
    IReadOnlyList<UndoChange> Changes,
    IReadOnlyList<UndoProblem> Problems,
    bool PerformanceWaitsForGameMode = false,
    bool GameModeNotKnown = false);

/// <summary>What Windows said about its restore points when Undo Changes last asked.</summary>
/// <param name="Newest">The newest System Restore point, or null when there is none or Windows would not list them.</param>
/// <param name="Listed">True when Windows answered at all. It lists restore points only to an administrator.</param>
public sealed record RestorePointLook(RestorePoint? Newest, bool Listed);

/// <summary>How putting a change back went.</summary>
public enum UndoOutcomeKind
{
    /// <summary>Everything the question listed was put back.</summary>
    PutBack,

    /// <summary>Some of it was put back, and the message says what was not.</summary>
    PartlyPutBack,

    /// <summary>Nothing changed, because nothing was left to put back.</summary>
    NothingLeft,

    /// <summary>Nothing changed, because what is there now is not what the question described.</summary>
    ChangedSinceListed,

    /// <summary>Nothing changed, because something else SysManager is doing would collide with it.</summary>
    Busy,

    /// <summary>It could not be put back, and the message says why.</summary>
    Failed,
}

/// <summary>The result of putting one change back.</summary>
/// <param name="Kind">How it went.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record UndoOutcome(UndoOutcomeKind Kind, string Message)
{
    /// <summary>True when something on the PC changed, even if not everything did.</summary>
    public bool ChangedSomething => Kind is UndoOutcomeKind.PutBack or UndoOutcomeKind.PartlyPutBack;
}
