// SysManager · IGamingTweak
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// One reversible optimization in a game-mode profile (power plan, visual effects, timer
/// resolution, CPU affinity/priority, standby purge, search indexing, notifications). A tweak
/// is constructed with the ORIGINAL state to restore (machine-wide originals come from the
/// <c>GamingSnapshot</c> the service captured up front; per-game originals are read when the
/// step is built), applies the change on <see cref="ApplyAsync"/>, and restores exactly that
/// original on <see cref="RevertAsync"/>. Injecting the original (rather than self-capturing)
/// means the in-session revert and the crash-recovery revert share ONE path: recovery simply
/// rebuilds the machine-wide tweaks from the persisted snapshot and calls
/// <see cref="RevertAsync"/>. Modelling every step behind this seam also lets the ordering,
/// admin-degradation, failure-isolation, and revert-in-reverse logic in
/// <see cref="GamingProfileService"/> be unit-tested with fake steps — no real system calls.
/// </summary>
public interface IGamingTweak
{
    /// <summary>Short user-facing label (e.g. "Ultimate Performance plan").</summary>
    string Label { get; }

    /// <summary>
    /// True if this step needs an elevated token to apply. When the app is not elevated the
    /// engine skips it and reports it as skipped-for-admin rather than attempting and failing.
    /// </summary>
    bool RequiresAdmin { get; }

    /// <summary>
    /// Apply the optimization. Returns true if applied; false if it was a no-op (already in
    /// the desired state) or could not be applied for a non-fatal reason. Must not throw for
    /// expected failures — return false. A throwing step is isolated by the engine (logged;
    /// the batch continues) so one bad step never aborts the rest or the revert.
    /// </summary>
    Task<bool> ApplyAsync(CancellationToken ct);

    /// <summary>
    /// Undo the optimization, restoring the original state this tweak was given. Idempotent:
    /// a second call (or a call when the original is unknown / apply was a no-op) is a safe
    /// no-op. Called in REVERSE apply order by the engine.
    /// </summary>
    Task RevertAsync(CancellationToken ct);
}
