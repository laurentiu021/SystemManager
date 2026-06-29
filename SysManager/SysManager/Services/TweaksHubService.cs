// SysManager · TweaksHubService — unified front-end over the reversible privacy/UX tweaks
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Aggregates SysManager's existing reversible optimizations into one reviewable list with
/// selective apply/undo. It does NOT reimplement any tweak — it delegates to
/// <see cref="PrivacyService"/> (the same registry operations the Privacy &amp; Telemetry tab
/// uses, each individually reversible) and creates an automatic System Restore point (via
/// <see cref="RestorePointService"/>) before the first change in a session. Nothing is
/// written to the system without an explicit Apply/Undo from the user.
/// </summary>
public sealed class TweaksHubService : ITweaksHubService
{
    private readonly PrivacyService _privacy;
    private readonly RestorePointService _restore;
    private bool _restorePointAttemptedThisSession;

    public TweaksHubService(PrivacyService privacy, RestorePointService restore)
    {
        _privacy = privacy;
        _restore = restore;
    }

    /// <summary>Loads every tweak with its current applied state, classified into tiers.</summary>
    public IReadOnlyList<TweakItem> LoadTweaks()
        => _privacy.LoadToggles().Select(TweakItem.From).ToList();

    /// <summary>
    /// Applies (enable=true) or reverts (enable=false) the given tweaks. Attempts a restore
    /// point before the first change in the session (best-effort — a failure to snapshot does
    /// not block the change). Returns the items that failed to write AND whether a restore
    /// point was actually created, so the caller can report honestly instead of over-promising.
    /// </summary>
    public async Task<TweakApplyResult> ApplyAsync(IReadOnlyList<TweakItem> tweaks, bool enable, CancellationToken ct = default)
    {
        if (tweaks.Count == 0) return new TweakApplyResult([], false);

        // ConfigureAwait(true): this is invoked from a UI-thread command and the post-await
        // loop mutates bound TweakItem.IsApplied (and the VM's pending counts + command
        // CanExecute). Resuming on the captured UI context keeps those mutations on the UI
        // thread — RestorePointService.CreateAsync hops to the thread pool internally, so
        // without this the continuation would run off-thread (a real cross-thread defect).
        bool restorePointCreated = await EnsureRestorePointAsync(ct).ConfigureAwait(true);

        var failed = new List<TweakItem>();
        foreach (var item in tweaks)
        {
            item.Toggle.IsEnabled = enable;
            if (_privacy.ApplyToggle(item.Toggle))
                item.IsApplied = enable;
            else
                failed.Add(item);
        }
        return new TweakApplyResult(failed, restorePointCreated);
    }

    /// <summary>
    /// Attempts a restore point once per session. Returns true only if one was actually
    /// created. Best-effort: System Restore needs admin and is rate-limited by Windows to one
    /// per 24h, so a "no" is normal and must not be presented as a guaranteed safety net.
    /// </summary>
    private async Task<bool> EnsureRestorePointAsync(CancellationToken ct)
    {
        if (_restorePointAttemptedThisSession) return false;
        // Mark attempted first so a slow/failed snapshot isn't retried before every batch.
        _restorePointAttemptedThisSession = true;
        try
        {
            return await _restore.CreateAsync("SysManager Tweaks Hub", ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Debug("Tweaks Hub restore point skipped: {Error}", ex.Message);
            return false;
        }
    }

    // ── Pure helpers (unit-testable) ───────────────────────────────────────

    /// <summary>Count of selected tweaks not yet applied (would be turned on by Apply Selected).</summary>
    public static int PendingApplyCount(IEnumerable<TweakItem> tweaks)
        => tweaks.Count(t => t.IsSelected && !t.IsApplied);

    /// <summary>Count of selected tweaks currently applied (would be reverted by Undo Selected).</summary>
    public static int PendingUndoCount(IEnumerable<TweakItem> tweaks)
        => tweaks.Count(t => t.IsSelected && t.IsApplied);
}
