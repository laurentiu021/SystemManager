// SysManager · IGamingProfileService — testable seam for Gaming Profile
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Seam over <see cref="GamingProfileService"/> so <c>GamingProfileViewModel</c> can be
/// unit-tested with a substituted implementation (no real power/timer/registry/service
/// mutations). Mirrors the established interface-seam pattern
/// (<see cref="IAudioMixerService"/>, <see cref="IPrivacyService"/>).
///
/// <para>The service is a pure ORCHESTRATOR: it composes already-audited SysManager services
/// into an ordered set of reversible steps and applies/reverts them as a unit. It never
/// reimplements the underlying tweaks.</para>
/// </summary>
public interface IGamingProfileService
{
    /// <summary>True while a game-mode profile is currently applied (tweaks are in effect).</summary>
    bool IsActive { get; }

    /// <summary>
    /// The running process the current session is bound to (its exit auto-reverts), or null
    /// when no session is active or the session isn't bound to a specific game.
    /// </summary>
    int? BoundGamePid { get; }

    /// <summary>
    /// Apply <paramref name="profile"/>, optionally targeting a specific game process for
    /// affinity/priority and auto-revert-on-exit. Captures original state first (best-effort
    /// System Restore point too), then applies each enabled step in order. Steps that need
    /// admin while the app is not elevated are skipped and reported, not failed. Returns a
    /// per-step outcome so the UI can report honestly. A game that closed while all that ran
    /// ends the session before this returns (<see cref="GamingApplyResult.EndedAtStart"/>).
    /// </summary>
    Task<GamingApplyResult> ApplyAsync(GamingProfile profile, GameTarget? game, CancellationToken ct = default);

    /// <summary>
    /// What the Start confirmation adds about the restore point <see cref="ApplyAsync"/> tries to take first, or
    /// an empty string when it will not try. See <see cref="ISessionRestorePoint.ConfirmationNotice"/>.
    /// </summary>
    string RestorePointNotice { get; }

    /// <summary>
    /// Revert the active session: undo every applied step in REVERSE order and clear the
    /// persisted active-session record. Idempotent — safe to call with no active session.
    /// Every step is attempted even when one fails, and the result names the ones that could
    /// not be restored, so a caller never announces a full restore it did not get.
    /// </summary>
    Task<GamingRevertResult> RevertAsync(CancellationToken ct = default);

    /// <summary>Load the last-used configuration (restored into the UI on launch).</summary>
    GamingProfile LoadLastConfig();

    /// <summary>Persist the last-used configuration so it's remembered across launches.</summary>
    void SaveLastConfig(GamingProfile profile);

    /// <summary>
    /// What a previous run left on disk: a session still applied (closed or crashed mid-game), which the UI offers to
    /// revert on startup — crash recovery for the machine-wide tweaks — or none, or a record that could not be read or
    /// used. None while game mode is on in this run.
    /// </summary>
    /// <remarks>
    /// A record that could not be read is not "none". Undo Changes holds Performance Mode back for it, as for a session
    /// that was left on (#1525): Performance Mode put back under a session still to be recovered would be undone by that
    /// recovery. One that was read and cannot be used holds nothing this build can recover.
    /// </remarks>
    PendingRecovery ReadPendingRecovery();

    /// <summary>
    /// True the first time it is asked in this run, false after. The question about a session a previous run left on is
    /// asked once a run, by whichever gets there first: the Dashboard when SysManager starts, or Gaming Profile if it is
    /// opened before that (#2592).
    /// </summary>
    bool ClaimRecoveryQuestion();

    /// <summary>
    /// Revert a leftover session found on disk from a previous run (crash recovery). Reports
    /// what could not be restored, as <see cref="RevertAsync"/> does.
    /// </summary>
    /// <exception cref="System.IO.IOException">
    /// The record of the session could not be read just now. Nothing was reverted, and the record is kept.
    /// </exception>
    Task<GamingRevertResult> RecoverPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Raised (on the captured context) when the bound game exits and the session auto-reverts,
    /// carrying what that revert could not restore.
    /// </summary>
    event EventHandler<GamingRevertResult>? SessionAutoReverted;
}

/// <summary>What a previous run's record of a game mode session says.</summary>
public enum PendingRecoveryKind
{
    /// <summary>No session was left on, or game mode is on now, in this run. The default.</summary>
    None,

    /// <summary>A session was left on, and its changes are still to be put back.</summary>
    LeftOn,

    /// <summary>The record is there and could not be read just now.</summary>
    Unreadable,

    /// <summary>The record was read and this build cannot use it: it does not parse, or a newer SysManager wrote it.</summary>
    Unusable,
}

/// <summary>A previous run's record of a game mode session, as <see cref="IGamingProfileService.ReadPendingRecovery"/> found it.</summary>
/// <param name="Kind">What the record says.</param>
/// <param name="NeedsAdmin">
/// True when a session left on paused search indexing: putting it back starts the indexer again, which only an
/// administrator can.
/// </param>
public readonly record struct PendingRecovery(PendingRecoveryKind Kind, bool NeedsAdmin = false);

/// <summary>A running process chosen as the game target for affinity/priority + auto-revert.</summary>
/// <param name="ProcessId">The game's process ID.</param>
/// <param name="Name">The game's process name, for the prompt and the status line.</param>
/// <param name="StartTime">
/// When the game started, as listed, or null when Windows would not say. With the ID it names the game: Windows gives
/// a closed process's ID to the next one started, and game mode can stay on for hours, so every per-game change,
/// restore and the auto-revert binding check it (#2559).
/// </param>
public sealed record GameTarget(int ProcessId, string Name, DateTime? StartTime);

/// <summary>The outcome of one step in an apply batch.</summary>
public enum GamingStepStatus
{
    /// <summary>Applied successfully.</summary>
    Applied,

    /// <summary>Skipped because it needs administrator and the app is not elevated.</summary>
    SkippedNeedsAdmin,

    /// <summary>
    /// A no-op — the system was already in the desired state (or the step had nothing to do).
    /// Not a failure: nothing changed and nothing needs reverting, so it is not surfaced as an error.
    /// </summary>
    SkippedNoChange,

    /// <summary>Attempted but failed (message in <see cref="GamingStepOutcome.Message"/>).</summary>
    Failed,
}

/// <summary>Per-step apply outcome (label + status + optional message).</summary>
public sealed record GamingStepOutcome(string Label, GamingStepStatus Status, string Message = "");

/// <summary>
/// Result of an <see cref="IGamingProfileService.ApplyAsync"/> batch: the per-step outcomes
/// and whether a System Restore point was actually created (so the UI never over-promises a
/// safety net that didn't materialize).
/// </summary>
/// <param name="Steps">Per-step outcomes; empty when the batch never ran.</param>
/// <param name="RestorePointCreated">Whether a restore point was actually created.</param>
/// <param name="BlockedBy">
/// Name of the operation that already held the system-modification lock, or <c>null</c> when the
/// batch ran. Non-null means NOTHING was changed and no snapshot was taken — Gaming Profile and
/// Performance Mode write the same power plan and visual-effects settings while each keeps its own
/// idea of the original, so whichever starts second must not capture the other's applied state as a
/// baseline it will later "restore".
/// </param>
/// <param name="StoreUnreadable">
/// True when the Gaming Profile store could not be read, so NOTHING was changed. The session's crash-recovery
/// record is written into that store, and it could not have been without writing over whatever the store held,
/// a leftover session's record among it (#2521).
/// </param>
/// <param name="GameClosed">
/// True when the chosen game had closed before anything changed, so NOTHING was changed. Its ID may belong to another
/// program by now, and game mode would have raised that program and waited for it to exit (#2559).
/// </param>
/// <param name="EndedAtStart">
/// Set when the game closed while game mode was starting, after the changes were made: the session was ended at once,
/// as the game's exit would have ended it, and this is that revert's result. Null otherwise. The exit had already
/// happened, so there was nothing left to wait for, and the session used to stay on until Stop (#2563).
/// </param>
public sealed record GamingApplyResult(
    IReadOnlyList<GamingStepOutcome> Steps,
    bool RestorePointCreated,
    string? BlockedBy = null,
    bool StoreUnreadable = false,
    bool GameClosed = false,
    GamingRevertResult? EndedAtStart = null)
{
    /// <summary>Count of steps that applied successfully.</summary>
    public int AppliedCount => Steps.Count(s => s.Status == GamingStepStatus.Applied);

    /// <summary>Count of steps skipped for lack of administrator rights.</summary>
    public int SkippedForAdminCount => Steps.Count(s => s.Status == GamingStepStatus.SkippedNeedsAdmin);

    /// <summary>Count of steps that were attempted and failed.</summary>
    public int FailedCount => Steps.Count(s => s.Status == GamingStepStatus.Failed);
}

/// <summary>
/// Result of a revert — a Stop, the automatic revert when the game exits, or the crash-recovery
/// sweep. Each step's undo is isolated so one failure never strands the others; this is how the
/// caller learns about the failure, rather than announcing that everything was restored (#2445).
/// </summary>
/// <param name="NotRestored">Labels of the steps whose undo failed; empty when every step was restored.</param>
public sealed record GamingRevertResult(IReadOnlyList<string> NotRestored)
{
    /// <summary>Everything was restored — also the result when there was nothing to revert.</summary>
    public static GamingRevertResult Complete { get; } = new([]);

    /// <summary>True when every step was restored.</summary>
    public bool FullyRestored => NotRestored.Count == 0;

    /// <summary>
    /// The status line after a revert: <paramref name="fullyRestored"/> when every step came back, otherwise
    /// <paramref name="partialLead"/> followed by the settings that did not, so a failed undo is never announced as a
    /// restore (#2445).
    /// </summary>
    /// <remarks>
    /// Here rather than on the Gaming Profile tab because Undo Changes turns game mode off too (#1525), and both have
    /// to say the same thing about a revert that did not fully work.
    /// </remarks>
    public string Describe(string fullyRestored, string partialLead)
    {
        if (FullyRestored) return fullyRestored;
        var one = NotRestored.Count == 1;
        var what = one
            ? $"\"{NotRestored[0]}\" was"
            : $"{NotRestored.Count} settings were ({string.Join(", ", NotRestored)})";
        return $"{partialLead}, but {what} not restored — check {(one ? "it" : "them")} yourself. The log has the reason.";
    }
}
