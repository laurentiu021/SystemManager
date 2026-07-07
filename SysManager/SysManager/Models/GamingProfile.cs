// SysManager · GamingProfile
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A "game mode" configuration — which reversible optimizations to apply when a game is
/// launched. Every toggle here maps to a step that composes an already-audited SysManager
/// service (power plan, visual effects, timer resolution, CPU affinity/priority, standby
/// purge, search indexing, notifications); nothing new is written to the system that isn't
/// undone on revert (the one exception, standby purge, is a non-destructive one-shot).
///
/// <para>Preview scope: this first cut is fully reversible. Killing background processes and
/// saved named multi-game profiles are intentionally not part of it (see the view banner).</para>
/// </summary>
public sealed record GamingProfile
{
    /// <summary>Switch to the Ultimate Performance power plan (restores the original plan on revert).</summary>
    public bool UltimatePerformancePlan { get; init; }

    /// <summary>Turn off window animations/fades/shadows for lower input latency (instant, no admin).</summary>
    public bool DisableVisualEffects { get; init; }

    /// <summary>Request the finest multimedia timer resolution (~0.5&#160;ms) while the game runs.</summary>
    public bool FinestTimerResolution { get; init; }

    /// <summary>Raise the selected game's CPU priority to High.</summary>
    public bool HighGameCpuPriority { get; init; }

    /// <summary>Pin the selected game to the performance cores (or all cores on a non-hybrid CPU).</summary>
    public bool PinGameToPerformanceCores { get; init; }

    /// <summary>Purge the Windows standby list to free cached RAM (one-shot, non-destructive, needs admin).</summary>
    public bool PurgeStandbyMemory { get; init; }

    /// <summary>Temporarily stop the Windows Search indexer (needs admin; restarted on revert if it was running).</summary>
    public bool PauseSearchIndexing { get; init; }

    /// <summary>Silence toast notifications while the game runs (HKCU, reversible, no admin).</summary>
    public bool SilenceNotifications { get; init; }

    /// <summary>A sensible default game-mode profile (conservative: affinity pinning off by default).</summary>
    public static GamingProfile Default => new()
    {
        UltimatePerformancePlan = true,
        DisableVisualEffects = true,
        FinestTimerResolution = true,
        HighGameCpuPriority = true,
        PinGameToPerformanceCores = false, // many games schedule better across all cores
        PurgeStandbyMemory = true,
        PauseSearchIndexing = true,
        SilenceNotifications = true,
    };

    /// <summary>True if at least one optimization is enabled (nothing to apply otherwise).</summary>
    public bool HasAnyEnabled =>
        UltimatePerformancePlan || DisableVisualEffects || FinestTimerResolution ||
        HighGameCpuPriority || PinGameToPerformanceCores || PurgeStandbyMemory ||
        PauseSearchIndexing || SilenceNotifications;
}

/// <summary>
/// The original MACHINE-WIDE system state captured immediately BEFORE a game-mode profile is
/// applied, so revert restores the exact baseline rather than a hardcoded default (mirrors
/// <c>PerformanceService.OriginalSnapshot</c>). Timer resolution is a per-process request
/// Windows releases when SysManager exits, so it needs no baseline here. Per-game CPU
/// affinity/priority originals are DELIBERATELY not stored: they self-clear when the game
/// exits, and restoring a saved mask to a since-recycled PID after a crash could hit the
/// wrong process — so they live only in memory on the live step, for a same-session revert.
/// This record is what gets persisted for crash recovery, and it is safe to replay on a
/// later launch because every field is machine-global, not tied to a volatile PID.
/// </summary>
public sealed record GamingSnapshot
{
    /// <summary>Active power-plan GUID before apply (null = power plan not part of this session).</summary>
    public string? OriginalPowerPlanGuid { get; init; }

    /// <summary>Whether UI visual effects were enabled before apply.</summary>
    public bool OriginalUiEffectsEnabled { get; init; } = true;

    /// <summary>Whether the Windows Search service was running before apply (null = not captured).</summary>
    public bool? SearchWasRunning { get; init; }

    /// <summary>The HKCU ToastEnabled DWORD before apply (null = value was absent → default on).</summary>
    public int? OriginalToastEnabled { get; init; }
}

/// <summary>A profile + the snapshot taken when it was applied — the on-disk record of a live session.</summary>
public sealed record GamingSessionRecord(GamingProfile Profile, GamingSnapshot Snapshot);

/// <summary>
/// The persisted Gaming Profile state (its own LocalAppData file — never shared with the
/// Performance tab's snapshot). Holds the last-used configuration so it's remembered across
/// launches, plus an active-session record that is present only while tweaks are applied.
/// A non-null <see cref="ActiveSession"/> found at startup means a previous run closed/crashed
/// mid-game and left tweaks applied — the app offers to revert them (crash-recovery sweep).
/// </summary>
public sealed record GamingProfileStore
{
    /// <summary>Schema version for forward-compatible loading (a newer file is rejected, not misread).</summary>
    public int SchemaVersion { get; init; }

    /// <summary>The last configuration the user applied, restored into the UI on next launch.</summary>
    public GamingProfile LastConfig { get; init; } = GamingProfile.Default;

    /// <summary>Non-null while a game-mode session is applied; used for crash recovery.</summary>
    public GamingSessionRecord? ActiveSession { get; init; }
}
