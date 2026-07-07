// SysManager · IGamingTweak
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// The result of applying a single <see cref="IGamingTweak"/> — distinguishes a genuine
/// change (track it for revert) from a benign no-op (nothing to do / already in the desired
/// state — do NOT report as a failure) from a real non-fatal failure. Modelling all three
/// keeps the engine from surfacing a harmless no-op to the user as "could not be applied".
/// </summary>
public enum GamingTweakResult
{
    /// <summary>The tweak changed system state — track it so revert can undo it.</summary>
    Applied,

    /// <summary>Nothing to do (already in the desired state / empty target) — benign, not a failure.</summary>
    NoChange,

    /// <summary>The tweak was attempted but could not be applied for a non-fatal reason.</summary>
    Failed,
}

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
    /// Apply the optimization. Returns <see cref="GamingTweakResult.Applied"/> when it changed
    /// state (the engine tracks it for revert), <see cref="GamingTweakResult.NoChange"/> for a
    /// benign no-op (already in the desired state / nothing to target — NOT surfaced as an
    /// error), or <see cref="GamingTweakResult.Failed"/> for a non-fatal failure. Must not throw
    /// for expected failures. A throwing step is isolated by the engine (logged; the batch
    /// continues) so one bad step never aborts the rest or the revert.
    /// </summary>
    Task<GamingTweakResult> ApplyAsync(CancellationToken ct);

    /// <summary>
    /// Undo the optimization, restoring the original state this tweak was given. Idempotent:
    /// a second call (or a call when the original is unknown / apply was a no-op) is a safe
    /// no-op. Called in REVERSE apply order by the engine.
    /// </summary>
    Task RevertAsync(CancellationToken ct);
}
