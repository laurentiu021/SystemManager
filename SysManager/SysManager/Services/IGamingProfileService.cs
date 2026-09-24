// SysManager · IGamingProfileService — testable seam for Gaming Profile
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Seam over <see cref="GamingProfileService"/> so <c>GamingProfileViewModel</c> can be
/// unit-tested with a substituted implementation (no real power/timer/registry/service
/// mutations). Mirrors the established interface-seam pattern
/// (<see cref="IAudioMixerService"/>, <see cref="ITweaksHubService"/>).
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
    /// per-step outcome so the UI can report honestly.
    /// </summary>
    Task<GamingApplyResult> ApplyAsync(GamingProfile profile, GameTarget? game, CancellationToken ct = default);

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
    /// True if a previous run left a session applied on disk (closed/crashed mid-game). The
    /// UI offers to revert it on startup — crash recovery for the machine-wide tweaks.
    /// </summary>
    bool HasPendingRecovery { get; }

    /// <summary>
    /// Revert a leftover session found on disk from a previous run (crash recovery). Reports
    /// what could not be restored, as <see cref="RevertAsync"/> does.
    /// </summary>
    Task<GamingRevertResult> RecoverPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Raised (on the captured context) when the bound game exits and the session auto-reverts,
    /// carrying what that revert could not restore.
    /// </summary>
    event EventHandler<GamingRevertResult>? SessionAutoReverted;
}

/// <summary>A running process chosen as the game target for affinity/priority + auto-revert.</summary>
public sealed record GameTarget(int ProcessId, string Name);

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
/// safety net that didn't materialize — mirrors <see cref="TweakApplyResult"/>).
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
public sealed record GamingApplyResult(
    IReadOnlyList<GamingStepOutcome> Steps,
    bool RestorePointCreated,
    string? BlockedBy = null)
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
}
