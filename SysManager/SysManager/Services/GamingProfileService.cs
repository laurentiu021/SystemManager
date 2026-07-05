// SysManager · GamingProfileService — one-click reversible game mode (orchestrator)
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Applies and reverts a "game mode" — a bundle of reversible optimizations — by composing
/// SysManager's already-audited services (<see cref="PerformanceService"/>,
/// <see cref="ITimerResolutionService"/>, <see cref="ICpuAffinityService"/>,
/// <see cref="StandbyMemoryService"/>, <see cref="ServiceManagerService"/>, and the HKCU
/// notifications key) into an ordered set of <see cref="IGamingTweak"/> steps. It never
/// reimplements a tweak.
///
/// <para>SAFETY CONTRACT (v1 preview — fully reversible):</para>
/// <list type="bullet">
/// <item>A machine-wide <see cref="GamingSnapshot"/> is captured BEFORE the first change and
///   persisted to its OWN LocalAppData file (never the Performance tab's snapshot).</item>
/// <item>A best-effort System Restore point is taken once per session.</item>
/// <item>Revert undoes every applied step in REVERSE order, restoring the captured originals.</item>
/// <item>Because the snapshot lives on disk, a crash/close mid-game is recoverable: on next
///   launch the machine-wide tweaks are rebuilt from the snapshot and reverted.</item>
/// <item>Steps that need admin while the app is not elevated are skipped and reported, never
///   silently failed.</item>
/// </list>
/// </summary>
public sealed class GamingProfileService : IGamingProfileService, IDisposable
{
    internal const int CurrentSchemaVersion = 1;

    private readonly PerformanceService _performance;
    private readonly ITimerResolutionService _timer;
    private readonly ICpuAffinityService _cpu;
    private readonly StandbyMemoryService _standby;
    private readonly RestorePointService _restore;
    private readonly bool _isElevated;
    private readonly string _storePath;

    // Steps of the live session, in apply order (reverted in reverse). Empty when inactive.
    private readonly List<IGamingTweak> _appliedSteps = new();
    private Process? _boundGame;
    private bool _restorePointAttemptedThisSession;
    private bool _disposed;

    public GamingProfileService(
        PerformanceService performance,
        ITimerResolutionService timer,
        ICpuAffinityService cpu,
        StandbyMemoryService standby,
        RestorePointService restore,
        bool isElevated,
        string? storePath = null)
    {
        _performance = performance;
        _timer = timer;
        _cpu = cpu;
        _standby = standby;
        _restore = restore;
        _isElevated = isElevated;
        _storePath = storePath ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "gaming-profiles.json");
    }

    public bool IsActive => _appliedSteps.Count > 0;
    public int? BoundGamePid { get; private set; }
    public bool HasPendingRecovery => !IsActive && LoadStore().ActiveSession is not null;

    public event EventHandler? SessionAutoReverted;

    // ── The engine (pure, unit-testable with fake IGamingTweak steps) ──────────

    /// <summary>
    /// Apply each enabled step in order. Admin-only steps are skipped-for-admin when
    /// <paramref name="isElevated"/> is false. A step that throws is isolated (logged) and
    /// reported as failed — the batch continues so one bad step never aborts the rest. The
    /// applied steps are appended to <paramref name="applied"/> in apply order for later
    /// reverse-order revert.
    /// </summary>
    internal static async Task<List<GamingStepOutcome>> RunApplyAsync(
        IReadOnlyList<IGamingTweak> steps, bool isElevated, List<IGamingTweak> applied, CancellationToken ct)
    {
        var outcomes = new List<GamingStepOutcome>(steps.Count);
        foreach (var step in steps)
        {
            if (step.RequiresAdmin && !isElevated)
            {
                outcomes.Add(new GamingStepOutcome(step.Label, GamingStepStatus.SkippedNeedsAdmin,
                    "Needs administrator."));
                continue;
            }

            try
            {
                bool ok = await step.ApplyAsync(ct).ConfigureAwait(false);
                if (ok)
                {
                    applied.Add(step);
                    outcomes.Add(new GamingStepOutcome(step.Label, GamingStepStatus.Applied));
                }
                else
                {
                    outcomes.Add(new GamingStepOutcome(step.Label, GamingStepStatus.Failed,
                        "Could not be applied."));
                }
            }
            catch (Exception ex)
            {
                // Isolate a faulting step: log and continue so the rest of the batch (and the
                // eventual revert of what DID apply) still runs.
                Log.Warning(ex, "Gaming Profile step '{Label}' threw during apply", step.Label);
                outcomes.Add(new GamingStepOutcome(step.Label, GamingStepStatus.Failed, ex.Message));
            }
        }
        return outcomes;
    }

    /// <summary>
    /// Revert the given applied steps in REVERSE order. Each revert is isolated so one failure
    /// doesn't strand the others. Idempotent at the step level (each step's RevertAsync is a
    /// safe no-op when it has nothing to undo).
    /// </summary>
    internal static async Task RunRevertAsync(IReadOnlyList<IGamingTweak> applied, CancellationToken ct)
    {
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            try { await applied[i].RevertAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Gaming Profile step '{Label}' threw during revert", applied[i].Label);
            }
        }
    }

    // ── Apply / Revert (real steps + persistence + auto-revert) ────────────────

    public async Task<GamingApplyResult> ApplyAsync(GamingProfile profile, GameTarget? game, CancellationToken ct = default)
    {
        if (IsActive) await RevertAsync(ct).ConfigureAwait(false); // never stack sessions

        bool restorePointCreated = await EnsureRestorePointAsync(ct).ConfigureAwait(true);

        // Capture the machine-wide baseline BEFORE any change, and persist it so a crash
        // mid-game is still recoverable.
        var snapshot = await CaptureSnapshotAsync(profile, ct).ConfigureAwait(true);
        SaveStore(LoadStore() with { ActiveSession = new GamingSessionRecord(profile, snapshot) });

        var steps = BuildSteps(profile, game, snapshot);
        var outcomes = await RunApplyAsync(steps, _isElevated, _appliedSteps, ct).ConfigureAwait(true);

        BoundGamePid = game?.ProcessId;
        BindAutoRevert(game);

        Log.Information("Gaming Profile applied: {Applied} applied, {Admin} need admin, {Failed} failed",
            outcomes.Count(o => o.Status == GamingStepStatus.Applied),
            outcomes.Count(o => o.Status == GamingStepStatus.SkippedNeedsAdmin),
            outcomes.Count(o => o.Status == GamingStepStatus.Failed));

        return new GamingApplyResult(outcomes, restorePointCreated);
    }

    public async Task RevertAsync(CancellationToken ct = default)
    {
        UnbindAutoRevert();
        BoundGamePid = null;

        if (_appliedSteps.Count > 0)
        {
            var applied = _appliedSteps.ToList();
            _appliedSteps.Clear();
            await RunRevertAsync(applied, ct).ConfigureAwait(true);
            Log.Information("Gaming Profile reverted {Count} step(s)", applied.Count);
        }

        // Clear the persisted active-session marker (whether or not steps were live).
        var store = LoadStore();
        if (store.ActiveSession is not null)
            SaveStore(store with { ActiveSession = null });
    }

    public GamingProfile LoadLastConfig() => LoadStore().LastConfig;

    public void SaveLastConfig(GamingProfile profile)
        => SaveStore(LoadStore() with { LastConfig = profile });

    public async Task RecoverPendingAsync(CancellationToken ct = default)
    {
        var store = LoadStore();
        if (store.ActiveSession is not { } session) return;

        // Rebuild ONLY the machine-wide tweaks from the persisted snapshot (per-game
        // affinity/priority are not persisted — a since-recycled PID must never be touched)
        // and revert them through the SAME engine path as an in-session revert.
        var steps = BuildMachineWideSteps(session.Profile, session.Snapshot);
        await RunRevertAsync(steps, ct).ConfigureAwait(true);
        SaveStore(store with { ActiveSession = null });
        Log.Information("Gaming Profile recovered a leftover session from a previous run");
    }

    // ── Snapshot + step construction ───────────────────────────────────────────

    private async Task<GamingSnapshot> CaptureSnapshotAsync(GamingProfile profile, CancellationToken ct)
    {
        string? planGuid = null;
        if (profile.UltimatePerformancePlan)
        {
            var (_, guid) = await _performance.GetActivePlanAsync(ct).ConfigureAwait(true);
            planGuid = guid;
        }

        return new GamingSnapshot
        {
            OriginalPowerPlanGuid = planGuid,
            OriginalUiEffectsEnabled = profile.DisableVisualEffects && PerformanceService.GetUiEffectsEnabled(),
            SearchWasRunning = profile.PauseSearchIndexing ? IsSearchRunning() : null,
            OriginalToastEnabled = profile.SilenceNotifications ? NotificationsTweak.ReadToastEnabled() : null,
        };
    }

    /// <summary>Build every enabled step in apply order (machine-wide first, then per-game).</summary>
    private List<IGamingTweak> BuildSteps(GamingProfile profile, GameTarget? game, GamingSnapshot snapshot)
    {
        var steps = BuildMachineWideSteps(profile, snapshot);

        // Per-game steps only when a game target was chosen.
        if (game is { } g)
        {
            if (profile.PinGameToPerformanceCores)
            {
                long target = PerformanceCoreMask(_cpu.GetCores());
                long? original = _cpu.GetAffinity(g.ProcessId);
                steps.Add(new GameAffinityTweak(_cpu, g.ProcessId, target, original));
            }
            if (profile.HighGameCpuPriority)
            {
                var original = _cpu.GetPriority(g.ProcessId);
                steps.Add(new GamePriorityTweak(_cpu, g.ProcessId, original));
            }
        }

        return steps;
    }

    /// <summary>The machine-wide steps — the ones the crash-recovery sweep can safely replay.</summary>
    private List<IGamingTweak> BuildMachineWideSteps(GamingProfile profile, GamingSnapshot snapshot)
    {
        var steps = new List<IGamingTweak>();
        if (profile.UltimatePerformancePlan) steps.Add(new PowerPlanTweak(_performance, snapshot.OriginalPowerPlanGuid));
        if (profile.DisableVisualEffects) steps.Add(new VisualEffectsTweak(snapshot.OriginalUiEffectsEnabled));
        if (profile.FinestTimerResolution) steps.Add(new TimerResolutionTweak(_timer));
        if (profile.PurgeStandbyMemory) steps.Add(new StandbyPurgeTweak(_standby));
        if (profile.PauseSearchIndexing) steps.Add(new SearchIndexingTweak(snapshot.SearchWasRunning ?? false));
        if (profile.SilenceNotifications) steps.Add(new NotificationsTweak(snapshot.OriginalToastEnabled));
        return steps;
    }

    /// <summary>Mask of the performance cores; all cores on a non-hybrid CPU (pure, testable).</summary>
    internal static long PerformanceCoreMask(IReadOnlyList<CpuCore> cores)
    {
        var perf = cores.Where(c => c.IsPerformance).Select(c => c.LogicalIndex).ToList();
        return perf.Count > 0
            ? CpuAffinityService.MaskFromIndices(perf)
            : CpuAffinityService.MaskFromIndices(cores.Select(c => c.LogicalIndex));
    }

    private static bool IsSearchRunning()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(SearchIndexingTweak.ServiceName);
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        // No such service / access denied / not a Windows service host → treat as "not running".
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private async Task<bool> EnsureRestorePointAsync(CancellationToken ct)
    {
        if (_restorePointAttemptedThisSession) return false;
        _restorePointAttemptedThisSession = true;
        try
        {
            return await _restore.CreateAsync("SysManager Gaming Profile", ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Debug("Gaming Profile restore point skipped: {Error}", ex.Message);
            return false;
        }
    }

    // ── Auto-revert on game exit (Process.Exited, NOT a poll loop) ─────────────

    private void BindAutoRevert(GameTarget? game)
    {
        if (game is null) return;
        try
        {
            var proc = Process.GetProcessById(game.ProcessId);
            proc.EnableRaisingEvents = true;
            proc.Exited += OnGameExited;
            _boundGame = proc;
        }
        // The game may have already exited between selection and bind, or be inaccessible —
        // leave the session unbound (manual revert still works) rather than crash.
        catch (ArgumentException ex) { Log.Debug("Gaming Profile could not bind auto-revert: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Debug("Gaming Profile could not bind auto-revert: {Error}", ex.Message); }
    }

    private void UnbindAutoRevert()
    {
        if (_boundGame is null) return;
        try { _boundGame.Exited -= OnGameExited; } catch (InvalidOperationException) { }
        _boundGame.Dispose();
        _boundGame = null;
    }

    private async void OnGameExited(object? sender, EventArgs e)
    {
        try
        {
            await RevertAsync().ConfigureAwait(true);
            SessionAutoReverted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Gaming Profile auto-revert on game exit failed");
        }
    }

    // ── Persistence (own file, versioned JSON — mirrors ProfileService idiom) ──

    private GamingProfileStore LoadStore()
    {
        try
        {
            if (!File.Exists(_storePath)) return new GamingProfileStore { SchemaVersion = CurrentSchemaVersion };
            var json = File.ReadAllText(_storePath);
            var store = JsonSerializer.Deserialize<GamingProfileStore>(json);
            if (store is null) return new GamingProfileStore { SchemaVersion = CurrentSchemaVersion };
            if (store.SchemaVersion > CurrentSchemaVersion)
            {
                // A newer build wrote this — don't misread it; start clean rather than corrupt.
                Log.Warning("Gaming profile store schema {Found} newer than {Known}; ignoring",
                    store.SchemaVersion, CurrentSchemaVersion);
                return new GamingProfileStore { SchemaVersion = CurrentSchemaVersion };
            }
            return store;
        }
        catch (IOException ex) { Log.Warning(ex, "Failed to read gaming profile store"); }
        catch (JsonException ex) { Log.Warning(ex, "Failed to parse gaming profile store"); }
        return new GamingProfileStore { SchemaVersion = CurrentSchemaVersion };
    }

    private void SaveStore(GamingProfileStore store)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                store with { SchemaVersion = CurrentSchemaVersion },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storePath, json);
        }
        catch (IOException ex) { Log.Warning(ex, "Failed to save gaming profile store"); }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Failed to save gaming profile store"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnbindAutoRevert();
    }
}
