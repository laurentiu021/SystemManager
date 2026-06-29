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
public sealed class TweaksHubService
{
    private readonly PrivacyService _privacy;
    private readonly RestorePointService _restore;
    private bool _restorePointTakenThisSession;

    public TweaksHubService(PrivacyService privacy, RestorePointService restore)
    {
        _privacy = privacy;
        _restore = restore;
    }

    /// <summary>Loads every tweak with its current applied state, classified into tiers.</summary>
    public IReadOnlyList<TweakItem> LoadTweaks()
        => _privacy.LoadToggles().Select(TweakItem.From).ToList();

    /// <summary>
    /// Applies (enable=true) or reverts (enable=false) the given tweaks. Creates a restore
    /// point before the first change in the session (best-effort — a failure to snapshot does
    /// not block the change, it's logged). Returns the items that failed to write.
    /// </summary>
    public async Task<IReadOnlyList<TweakItem>> ApplyAsync(IReadOnlyList<TweakItem> tweaks, bool enable, CancellationToken ct = default)
    {
        if (tweaks.Count == 0) return [];

        await EnsureRestorePointAsync(ct).ConfigureAwait(false);

        var failed = new List<TweakItem>();
        foreach (var item in tweaks)
        {
            item.Toggle.IsEnabled = enable;
            if (_privacy.ApplyToggle(item.Toggle))
                item.IsApplied = enable;
            else
                failed.Add(item);
        }
        return failed;
    }

    private async Task EnsureRestorePointAsync(CancellationToken ct)
    {
        if (_restorePointTakenThisSession) return;
        // Mark first so a slow/failed snapshot isn't retried before every batch — the restore
        // point is a best-effort safety net, not a hard gate (RestorePointService itself
        // rate-limits to one per 24h and needs admin; a miss is logged, not surfaced as failure).
        _restorePointTakenThisSession = true;
        try
        {
            await _restore.CreateAsync("SysManager Tweaks Hub", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Debug("Tweaks Hub restore point skipped: {Error}", ex.Message);
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
