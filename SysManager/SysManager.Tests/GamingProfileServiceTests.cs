// SysManager · GamingProfileServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the Gaming Profile ORCHESTRATION ENGINE — the risky part. The engine
/// (<see cref="GamingProfileService.RunApplyAsync"/> / <see cref="GamingProfileService.RunRevertAsync"/>)
/// is exercised with fake <see cref="IGamingTweak"/> steps so no real power/timer/registry/
/// service call happens: it verifies order, admin-degradation (skip-not-fail), failure
/// isolation (a throwing step never aborts the batch or the revert), and revert-in-REVERSE.
/// The pure helpers (<c>PerformanceCoreMask</c>) and the persisted-store round-trip / schema
/// guard are also covered here. The composed services themselves are already audited under
/// their own tests; this file treats them as a contract at the seam.
/// </summary>
public class GamingProfileServiceTests
{
    /// <summary>A fake step that records apply/revert order into a shared log.</summary>
    private sealed class FakeTweak(string label, List<string> log,
        bool requiresAdmin = false, GamingTweakResult applyResult = GamingTweakResult.Applied, bool throwOnApply = false) : IGamingTweak
    {
        public string Label => label;
        public bool RequiresAdmin => requiresAdmin;
        public bool Reverted { get; private set; }

        public Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
        {
            if (throwOnApply) throw new InvalidOperationException("boom");
            log.Add($"apply:{label}");
            return Task.FromResult(applyResult);
        }

        public Task RevertAsync(CancellationToken ct)
        {
            log.Add($"revert:{label}");
            Reverted = true;
            return Task.CompletedTask;
        }
    }

    // ── RunApplyAsync: order, admin-skip, no-op, failure isolation ──────────

    [Fact]
    public async Task RunApply_AppliesEnabledSteps_InOrder_AndTracksApplied()
    {
        var log = new List<string>();
        var applied = new List<IGamingTweak>();
        var steps = new IGamingTweak[]
        {
            new FakeTweak("a", log),
            new FakeTweak("b", log),
            new FakeTweak("c", log),
        };

        var outcomes = await GamingProfileService.RunApplyAsync(steps, isElevated: true, applied, default);

        Assert.Equal(["apply:a", "apply:b", "apply:c"], log);
        Assert.All(outcomes, o => Assert.Equal(GamingStepStatus.Applied, o.Status));
        Assert.Equal(3, applied.Count); // all recorded for later revert
    }

    [Fact]
    public async Task RunApply_AdminStep_WhenNotElevated_IsSkippedNotApplied()
    {
        var log = new List<string>();
        var applied = new List<IGamingTweak>();
        var steps = new IGamingTweak[]
        {
            new FakeTweak("user", log, requiresAdmin: false),
            new FakeTweak("admin", log, requiresAdmin: true),
        };

        var outcomes = await GamingProfileService.RunApplyAsync(steps, isElevated: false, applied, default);

        Assert.Equal(["apply:user"], log); // the admin step never ran
        Assert.Equal(GamingStepStatus.Applied, outcomes[0].Status);
        Assert.Equal(GamingStepStatus.SkippedNeedsAdmin, outcomes[1].Status);
        Assert.Single(applied); // skipped step is NOT queued for revert
    }

    [Fact]
    public async Task RunApply_NoOpStep_IsSkippedNoChange_NotFailed_NotTracked()
    {
        var log = new List<string>();
        var applied = new List<IGamingTweak>();
        // NoChange = "already in the desired state" → reported SkippedNoChange (NOT Failed — the
        // audit-1 fix) and NOT queued for revert (there's nothing to undo).
        var steps = new IGamingTweak[] { new FakeTweak("noop", log, applyResult: GamingTweakResult.NoChange) };

        var outcomes = await GamingProfileService.RunApplyAsync(steps, isElevated: true, applied, default);

        Assert.Equal(GamingStepStatus.SkippedNoChange, outcomes[0].Status);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task RunApply_FailedStep_IsFailed_NotTracked()
    {
        var log = new List<string>();
        var applied = new List<IGamingTweak>();
        // Failed = a genuine non-fatal failure → reported Failed and NOT queued for revert.
        var steps = new IGamingTweak[] { new FakeTweak("fail", log, applyResult: GamingTweakResult.Failed) };

        var outcomes = await GamingProfileService.RunApplyAsync(steps, isElevated: true, applied, default);

        Assert.Equal(GamingStepStatus.Failed, outcomes[0].Status);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task RunApply_ThrowingStep_IsIsolated_BatchContinues()
    {
        var log = new List<string>();
        var applied = new List<IGamingTweak>();
        var steps = new IGamingTweak[]
        {
            new FakeTweak("ok1", log),
            new FakeTweak("boom", log, throwOnApply: true),
            new FakeTweak("ok2", log),
        };

        var outcomes = await GamingProfileService.RunApplyAsync(steps, isElevated: true, applied, default);

        // The throwing step is reported failed, but ok2 still ran (batch not aborted).
        Assert.Equal(["apply:ok1", "apply:ok2"], log);
        Assert.Equal(GamingStepStatus.Applied, outcomes[0].Status);
        Assert.Equal(GamingStepStatus.Failed, outcomes[1].Status);
        Assert.Equal(GamingStepStatus.Applied, outcomes[2].Status);
        Assert.Equal(2, applied.Count); // only the two that succeeded are queued for revert
    }

    // ── RunRevertAsync: reverse order, isolation ───────────────────────────

    [Fact]
    public async Task RunRevert_RevertsAppliedSteps_InReverseOrder()
    {
        var log = new List<string>();
        var applied = new List<IGamingTweak>
        {
            new FakeTweak("a", log),
            new FakeTweak("b", log),
            new FakeTweak("c", log),
        };

        await GamingProfileService.RunRevertAsync(applied, default);

        Assert.Equal(["revert:c", "revert:b", "revert:a"], log);
    }

    [Fact]
    public async Task RunRevert_OneStepThrows_OthersStillRevert()
    {
        var log = new List<string>();
        // The middle step throws on revert; the engine must still revert the rest.
        var applied = new List<IGamingTweak>
        {
            new FakeTweak("a", log),
            new ThrowingRevertTweak("bad"),
            new FakeTweak("c", log),
        };

        await GamingProfileService.RunRevertAsync(applied, default);

        // c reverts first (reverse), bad throws (isolated), a still reverts.
        Assert.Equal(["revert:c", "revert:a"], log);
    }

    private sealed class ThrowingRevertTweak(string label) : IGamingTweak
    {
        public string Label => label;
        public bool RequiresAdmin => false;
        public Task<GamingTweakResult> ApplyAsync(CancellationToken ct) => Task.FromResult(GamingTweakResult.Applied);
        public Task RevertAsync(CancellationToken ct) => throw new InvalidOperationException("revert boom");
    }

    [Fact]
    public async Task RunRevert_EmptyList_IsNoOp()
    {
        await GamingProfileService.RunRevertAsync([], default); // must not throw
    }

    // ── PerformanceCoreMask (pure) ─────────────────────────────────────────

    [Fact]
    public void PerformanceCoreMask_HybridCpu_UsesOnlyPerformanceCores()
    {
        var cores = new List<CpuCore>
        {
            new(0, 1, "Performance"),
            new(1, 1, "Performance"),
            new(2, 0, "Efficiency"),
            new(3, 0, "Efficiency"),
        };
        // Only cores 0 and 1 → 0b0011.
        Assert.Equal(0b0011L, GamingProfileService.PerformanceCoreMask(cores));
    }

    [Fact]
    public void PerformanceCoreMask_NonHybridCpu_UsesAllCores()
    {
        var cores = new List<CpuCore>
        {
            new(0, 0, "Standard"),
            new(1, 0, "Standard"),
            new(2, 0, "Standard"),
        };
        // No performance cores → fall back to all → 0b0111.
        Assert.Equal(0b0111L, GamingProfileService.PerformanceCoreMask(cores));
    }

    [Fact]
    public void PerformanceCoreMask_Empty_IsZero()
        => Assert.Equal(0L, GamingProfileService.PerformanceCoreMask([]));

    // ── GamingProfile model semantics ──────────────────────────────────────

    [Fact]
    public void GamingProfile_HasAnyEnabled_TrueWhenAnyToggleSet()
    {
        Assert.False(new GamingProfile().HasAnyEnabled);
        Assert.True(new GamingProfile { SilenceNotifications = true }.HasAnyEnabled);
        Assert.True(GamingProfile.Default.HasAnyEnabled);
    }

    // ── Persistence round-trip + schema guard (own store file, temp path) ──

    // Builds a service pointed at a throwaway store file. The composed services are never
    // invoked in these tests (only LoadLastConfig/SaveLastConfig, which touch the file only),
    // so their construction is inert — no power/timer/registry call fires.
    private static GamingProfileService StoreOnlyService(string path)
    {
        var runner = new PowerShellRunner();
        var restore = new RestorePointService(runner);
        return new GamingProfileService(
            new PerformanceService(runner, restore),
            new TimerResolutionService(),
            new CpuAffinityService(),
            new StandbyMemoryService(),
            restore,
            isElevated: false,
            storePath: path);
    }

    [Fact]
    public void SaveLastConfig_ThenLoad_RoundTripsTheConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-{Guid.NewGuid():N}.json");
        try
        {
            var svc = StoreOnlyService(path);
            var config = new GamingProfile { FinestTimerResolution = true, SilenceNotifications = true };
            svc.SaveLastConfig(config);

            // A fresh instance reads it back from disk (not from memory).
            var reloaded = StoreOnlyService(path).LoadLastConfig();
            Assert.True(reloaded.FinestTimerResolution);
            Assert.True(reloaded.SilenceNotifications);
            Assert.False(reloaded.UltimatePerformancePlan);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LoadLastConfig_NoFile_ReturnsDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-missing-{Guid.NewGuid():N}.json");
        Assert.False(File.Exists(path));
        // No file → the default config, never a crash.
        var cfg = StoreOnlyService(path).LoadLastConfig();
        Assert.Equal(GamingProfile.Default, cfg);
    }

    [Fact]
    public void LoadStore_NewerSchema_IsIgnored_NotMisread()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-newer-{Guid.NewGuid():N}.json");
        try
        {
            // A file written by a hypothetical future build (higher schema version).
            var future = JsonSerializer.Serialize(new
            {
                SchemaVersion = GamingProfileService.CurrentSchemaVersion + 1,
                LastConfig = new { SilenceNotifications = true },
            });
            File.WriteAllText(path, future);

            // Must NOT trust a newer file's fields — falls back to default rather than
            // half-reading a schema it doesn't understand.
            var cfg = StoreOnlyService(path).LoadLastConfig();
            Assert.Equal(GamingProfile.Default, cfg);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LoadLastConfig_MalformedJson_ReturnsDefault_NotCrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-bad-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json ][");
            var cfg = StoreOnlyService(path).LoadLastConfig();
            Assert.Equal(GamingProfile.Default, cfg);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Audit-1 fix: crash-recovery replays ONLY what actually applied ─────

    [Fact]
    public void EffectiveMachineWideProfile_OnlyIncludesAppliedMachineWideSteps()
    {
        // Requested everything, but only visual-effects + timer-resolution actually applied
        // (e.g. the admin-only WSearch + standby steps were skipped when unelevated). The
        // recovery profile must reflect ONLY what applied, so the next-launch sweep never
        // restarts a WSearch this run never stopped (the Audit-1 over-revert defect).
        var requested = new GamingProfile
        {
            UltimatePerformancePlan = true, DisableVisualEffects = true, FinestTimerResolution = true,
            PurgeStandbyMemory = true, PauseSearchIndexing = true, SilenceNotifications = true,
        };
        var applied = new IGamingTweak[]
        {
            new VisualEffectsTweak(true),
            new TimerResolutionTweak(Substitute.For<ITimerResolutionService>()),
        };

        var effective = GamingProfileService.EffectiveMachineWideProfile(requested, applied);

        Assert.True(effective.DisableVisualEffects);
        Assert.True(effective.FinestTimerResolution);
        Assert.False(effective.PauseSearchIndexing);   // was requested but skipped → NOT replayed
        Assert.False(effective.PurgeStandbyMemory);
        Assert.False(effective.UltimatePerformancePlan);
        Assert.False(effective.SilenceNotifications);
    }

    [Fact]
    public void EffectiveMachineWideProfile_NeverIncludesPerGameSteps()
    {
        var requested = new GamingProfile { HighGameCpuPriority = true, PinGameToPerformanceCores = true };
        var cpu = Substitute.For<ICpuAffinityService>();
        var applied = new IGamingTweak[]
        {
            new GamePriorityTweak(cpu, 1234, System.Diagnostics.ProcessPriorityClass.Normal),
            new GameAffinityTweak(cpu, 1234, 0b11L, 0b01L),
        };

        var effective = GamingProfileService.EffectiveMachineWideProfile(requested, applied);

        // Per-game toggles are never machine-wide → never in the recovery profile (a recycled
        // PID must never be touched on next launch).
        Assert.False(effective.HasAnyEnabled);
    }

    // ── Audit-1 fix: no-op is not counted as a failure in the result summary ─

    [Fact]
    public void GamingApplyResult_Counts_SeparateNoChangeFromFailed()
    {
        var result = new GamingApplyResult(
        [
            new GamingStepOutcome("a", GamingStepStatus.Applied),
            new GamingStepOutcome("b", GamingStepStatus.SkippedNoChange),
            new GamingStepOutcome("c", GamingStepStatus.SkippedNeedsAdmin),
            new GamingStepOutcome("d", GamingStepStatus.Failed),
        ], RestorePointCreated: false);

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(1, result.SkippedForAdminCount);
        Assert.Equal(1, result.FailedCount); // the no-op is NOT counted as failed
    }

    // ── Audit-2 fix: RevertAsync must not pin its gate-release continuation to the caller's
    //    SynchronizationContext (the shutdown UI-thread deadlock the SemaphoreSlim fix introduced) ─

    // A SynchronizationContext that queues posted callbacks but NEVER runs them — it stands in
    // for a UI Dispatcher thread that is blocked (as it is when Dispose does _gate.Wait() at
    // shutdown). If RevertAsync captured this context for its gate-release continuation, the
    // revert Task would never complete.
    private sealed class NonPumpingSyncContext : SynchronizationContext
    {
        public int PostCount;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref PostCount);
        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref PostCount);
    }

    // A step whose RevertAsync genuinely suspends off-thread, so RevertAsync's `await
    // RunRevertAsync(...)` is forced to schedule a continuation (which carries _gate.Release()).
    private sealed class SuspendingRevertTweak : IGamingTweak
    {
        public string Label => "suspending";
        public bool RequiresAdmin => false;
        public Task<GamingTweakResult> ApplyAsync(CancellationToken ct) => Task.FromResult(GamingTweakResult.Applied);
        public async Task RevertAsync(CancellationToken ct)
            => await Task.Run(() => Thread.Sleep(80), ct).ConfigureAwait(false);
    }

    [Fact]
    public void RevertAsync_GateReleaseContinuation_DoesNotDependOnCallerSyncContext()
    {
        // Reproduces the Audit-2 deadlock scenario deterministically: run RevertAsync on a thread
        // whose SynchronizationContext is never pumped (a stand-in for the shutdown-blocked UI
        // thread). With the ConfigureAwait(true) regression, the gate-release continuation is
        // posted to this context and the revert Task never completes → the wait below times out.
        // With the ConfigureAwait(false) fix, the continuation runs off-thread and it completes.
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-dl-{Guid.NewGuid():N}.json");
        bool completed = false;
        var worker = new Thread(() =>
        {
            var ctx = new NonPumpingSyncContext();
            SynchronizationContext.SetSynchronizationContext(ctx);

            var svc = StoreOnlyService(path);
            svc.SeedAppliedStepForTest(new SuspendingRevertTweak()); // IsActive is now true

            var revert = svc.RevertAsync();      // gate acquired inline; step suspends off-thread
            completed = revert.Wait(3000);       // no pump on this thread — fix makes it complete anyway
        });
        worker.IsBackground = true;
        worker.Start();
        worker.Join(6000);

        try
        {
            Assert.True(completed,
                "RevertAsync did not complete without pumping the caller's SynchronizationContext — " +
                "its gate-release continuation is pinned to the (blockable) caller thread (the Audit-2 deadlock).");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
