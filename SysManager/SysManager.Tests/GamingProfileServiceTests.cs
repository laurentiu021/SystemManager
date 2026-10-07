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
/// <para>In the serialized collection because the lock tests below take the process-wide
/// <c>OperationLockService.Instance</c>; a class holding that static in parallel with another
/// would make either one pass or fail for a foreign reason
/// (<see cref="ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection"/>).</para>
/// </summary>
[Collection("ProcessWideStatics")]
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

    [Fact]
    public async Task RunRevert_NamesTheStepThatCouldNotBeRestored()
    {
        // The isolation above used to end at a log line, so Stop, the game-exit revert and crash recovery all
        // announced a full restore over a step that had thrown (#2445).
        var log = new List<string>();
        var applied = new List<IGamingTweak>
        {
            new FakeTweak("a", log),
            new ThrowingRevertTweak("bad"),
            new FakeTweak("c", log),
        };

        var result = await GamingProfileService.RunRevertAsync(applied, default);

        Assert.False(result.FullyRestored);
        Assert.Equal(["bad"], result.NotRestored);
    }

    [Fact]
    public async Task RunRevert_WhenEveryStepReverts_IsFullyRestored()
    {
        var log = new List<string>();

        var result = await GamingProfileService.RunRevertAsync([new FakeTweak("a", log), new FakeTweak("b", log)], default);

        Assert.True(result.FullyRestored);
        Assert.Empty(result.NotRestored);
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
    private static GamingProfileService StoreOnlyService(
        string path, Func<string, CancellationToken, Task<bool>>? createRestorePoint = null,
        ICpuAffinityService? cpu = null, ITimerResolutionService? timer = null)
    {
        var runner = new PowerShellRunner();
        var restore = new RestorePointService(runner);
        return new GamingProfileService(
            // A folder of its own even though nothing here calls it: the two-argument constructor resolves the real
            // profile for its snapshot (#2555).
            new PerformanceService(runner, restore,
                Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"))),
            timer ?? new TimerResolutionService(),
            cpu ?? new CpuAffinityService(),
            new StandbyMemoryService(),
            // Never creates a point: these tests are about the engine, and a real System Restore
            // call needs admin, takes seconds and is rate-limited.
            new SessionRestorePoint(createRestorePoint ?? ((_, _) => Task.FromResult(false))),
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

    // ── A store that is there but cannot be read or used (#2521) ──────────
    //
    // LoadStore returned a fresh store for a file it could not read or parse, and SaveLastConfig, which runs on every
    // Start, wrote that fresh store back with the new configuration. A leftover session's crash-recovery record went
    // with it. The file is held open with delete sharing only for as long as a read must fail: the read fails, and the
    // replace a write ends with would still succeed, so a writer that refused can be told from one that could not.

    private static string PendingStore(GamingProfile lastConfig) => SerializePendingStore(new GamingProfileStore
    {
        LastConfig = lastConfig,
        ActiveSession = new GamingSessionRecord(new GamingProfile(), new GamingSnapshot()),
    });

    private static void DeleteStore(string path)
    {
        foreach (var file in new[] { path, path + ".unreadable" })
            if (File.Exists(file)) File.Delete(file);
    }

    [Fact]
    public void SaveLastConfig_WhenTheStoreCannotBeRead_WritesNothing_SoTheRecoveryRecordSurvives()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-held-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, PendingStore(new GamingProfile { SilenceNotifications = true }));
            var before = File.ReadAllBytes(path);
            var svc = StoreOnlyService(path);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
                svc.SaveLastConfig(new GamingProfile { FinestTimerResolution = true });

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(PendingRecoveryKind.LeftOn, StoreOnlyService(path).ReadPendingRecovery().Kind);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public void PendingRecovery_WhenTheStoreCannotBeRead_IsNotKnown_RatherThanNone()
    {
        // A leftover session in a store that could not be read just now is still there. Undo Changes must not take
        // "could not read" for "none": Performance Mode put back under it would be undone by its recovery (#1525).
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-unread-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, PendingStore(new GamingProfile()));
            var svc = StoreOnlyService(path);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
                Assert.Equal(PendingRecoveryKind.Unreadable, svc.ReadPendingRecovery().Kind);

            Assert.Equal(PendingRecoveryKind.LeftOn, svc.ReadPendingRecovery().Kind);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public void PendingRecovery_FromAStoreThatDoesNotParse_IsUnusable_NotNone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-unusable-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");

            Assert.Equal(PendingRecoveryKind.Unusable, StoreOnlyService(path).ReadPendingRecovery().Kind);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public void PendingRecovery_ThatPausedSearchIndexing_NeedsAdministratorRights()
    {
        // Its revert starts the indexer again, which only an administrator can.
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-indexing-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, SerializePendingStore(new GamingProfileStore
            {
                ActiveSession = new GamingSessionRecord(new GamingProfile { PauseSearchIndexing = true }, new GamingSnapshot()),
            }));
            Assert.Equal(new PendingRecovery(PendingRecoveryKind.LeftOn, NeedsAdmin: true),
                StoreOnlyService(path).ReadPendingRecovery());

            File.WriteAllText(path, PendingStore(new GamingProfile()));
            Assert.Equal(new PendingRecovery(PendingRecoveryKind.LeftOn, NeedsAdmin: false),
                StoreOnlyService(path).ReadPendingRecovery());
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public async Task RecoveringASession_WhoseRecordCannotBeRead_RevertsNothing_AndKeepsTheRecord()
    {
        // "Put back" was said of a session that was still on: the record had loaded as a fresh store.
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-recover-held-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, PendingStore(new GamingProfile()));
            var svc = StoreOnlyService(path);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
                await Assert.ThrowsAsync<IOException>(() => svc.RecoverPendingAsync());

            Assert.Equal(PendingRecoveryKind.LeftOn, svc.ReadPendingRecovery().Kind);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public void SaveLastConfig_OverAStoreThatDoesNotParse_KeepsItAside_ThenSaves()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-aside-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json ][");

            StoreOnlyService(path).SaveLastConfig(new GamingProfile { FinestTimerResolution = true });

            Assert.Equal("{ this is not valid json ][", File.ReadAllText(path + ".unreadable"));
            Assert.True(StoreOnlyService(path).LoadLastConfig().FinestTimerResolution);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public void SaveLastConfig_WhenAStoreThatDoesNotParseCannotBeSetAside_WritesNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-stuck-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json ][");
            // A folder where the set-aside copy would go: the move fails, and a write to the file itself would
            // not.
            Directory.CreateDirectory(path + ".unreadable");

            StoreOnlyService(path).SaveLastConfig(new GamingProfile { FinestTimerResolution = true });

            Assert.Equal("{ this is not valid json ][", File.ReadAllText(path));
        }
        finally
        {
            DeleteStore(path);
            if (Directory.Exists(path + ".unreadable")) Directory.Delete(path + ".unreadable");
        }
    }

    [Fact]
    public void SaveLastConfig_OverAStoreANewerSysManagerWrote_KeepsItAside_ThenSaves()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-newer-aside-{Guid.NewGuid():N}.json");
        try
        {
            var future = JsonSerializer.Serialize(new
            {
                SchemaVersion = GamingProfileService.CurrentSchemaVersion + 1,
                LastConfig = new { SilenceNotifications = true },
            });
            File.WriteAllText(path, future);

            StoreOnlyService(path).SaveLastConfig(new GamingProfile { FinestTimerResolution = true });

            Assert.Equal(future, File.ReadAllText(path + ".unreadable"));
            Assert.True(StoreOnlyService(path).LoadLastConfig().FinestTimerResolution);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public async Task ApplyAsync_WhenTheStoreCannotBeRead_IsRefusedBeforeAnyChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-held-apply-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, PendingStore(new GamingProfile()));
            var before = File.ReadAllBytes(path);
            var restorePointAttempts = 0;
            var svc = StoreOnlyService(path, (_, _) =>
            {
                restorePointAttempts++;
                return Task.FromResult(false);
            });

            GamingApplyResult result;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
            {
                // Only a step that needs administrator, which this service is not: were the refusal missing, the
                // batch would still change nothing on this machine.
                result = await svc.ApplyAsync(new GamingProfile { PurgeStandbyMemory = true }, game: null);
            }

            Assert.True(result.StoreUnreadable);
            Assert.Empty(result.Steps);
            Assert.Equal(0, restorePointAttempts);
            Assert.False(svc.IsActive);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public async Task RevertAsync_WhenTheStoreCannotBeRead_StillReverts_AndKeepsTheRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-held-revert-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, PendingStore(new GamingProfile()));
            var before = File.ReadAllBytes(path);
            var svc = StoreOnlyService(path);
            var tweak = new FakeTweak("power plan", []);
            svc.SeedAppliedStepForTest(tweak);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
                await svc.RevertAsync();

            // The settings came back; the record stays, so the next launch offers the restore again rather than
            // having the store written over.
            Assert.True(tweak.Reverted);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { DeleteStore(path); }
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
            UltimatePerformancePlan = true,
            DisableVisualEffects = true,
            FinestTimerResolution = true,
            PurgeStandbyMemory = true,
            PauseSearchIndexing = true,
            SilenceNotifications = true,
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
        var game = new GameTarget(1234, "game.exe", StartTime: null);
        var applied = new IGamingTweak[]
        {
            new GamePriorityTweak(cpu, game, System.Diagnostics.ProcessPriorityClass.Normal),
            new GameAffinityTweak(cpu, game, 0b11L, 0b01L),
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

    // A step whose RevertAsync suspends on a signal the test controls (no wall-clock sleep), so
    // RevertAsync's `await RunRevertAsync(...)` is forced to schedule a real off-thread
    // continuation (the one that carries _gate.Release()). Deterministic: the test decides exactly
    // when the revert resumes, so the outcome never depends on timing or thread-pool scheduling.
    private sealed class GatedRevertTweak : IGamingTweak
    {
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once RevertAsync has begun awaiting this step (it is now suspended).</summary>
        public Task Entered => _entered.Task;

        /// <summary>Releases the suspended RevertAsync so its gate-release continuation runs.</summary>
        public void Resume() => _resume.TrySetResult();

        public string Label => "gated";
        public bool RequiresAdmin => false;
        public Task<GamingTweakResult> ApplyAsync(CancellationToken ct) => Task.FromResult(GamingTweakResult.Applied);
        public async Task RevertAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _resume.Task.ConfigureAwait(false);
        }
    }

    // Awaits <paramref name="task"/>, returning false instead of hanging if it does not finish
    // within the deadlock backstop — the async, non-blocking equivalent of Task.Wait(timeout)
    // (so the xUnit1031 "no blocking task operations in tests" analyzer stays satisfied).
    private static async Task<bool> CompletesWithinAsync(Task task, int ms = 10_000)
    {
        try { await task.WaitAsync(TimeSpan.FromMilliseconds(ms)).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    [Fact]
    public async Task RevertAsync_GateReleaseContinuation_DoesNotDependOnCallerSyncContext()
    {
        // Reproduces the Audit-2 deadlock scenario deterministically: RevertAsync runs on a thread
        // whose SynchronizationContext is never pumped (a stand-in for the shutdown-blocked UI
        // thread). With the ConfigureAwait(true) regression the gate-release continuation is posted
        // to that context — PostCount rises and the revert Task never completes; with the
        // ConfigureAwait(false) fix the continuation runs off-thread, nothing is posted, and it
        // completes. The suspension is signalled (no Thread.Sleep), so the pass path is near-instant
        // and not thread-pool-timing dependent — the only timeout is the genuine-deadlock backstop.
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-dl-{Guid.NewGuid():N}.json");
        var ctx = new NonPumpingSyncContext();
        var tweak = new GatedRevertTweak();
        Task? revert = null;

        var worker = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(ctx);
            var svc = StoreOnlyService(path);
            svc.SeedAppliedStepForTest(tweak);   // IsActive is now true
            revert = svc.RevertAsync();          // gate acquired inline; the step suspends on _resume
        });
        worker.IsBackground = true;
        worker.Start();
        // RevertAsync returns an incomplete Task synchronously (it awaits, it does not block), so the
        // worker returns promptly; a successful Join also publishes the `revert` write to this thread.
        Assert.True(worker.Join(10_000),
            "worker did not return — RevertAsync blocked synchronously instead of suspending");

        try
        {
            // Deterministic handshake: wait until RevertAsync is actually parked inside the step,
            // then release it. The continuation that follows is the one carrying _gate.Release().
            Assert.True(await CompletesWithinAsync(tweak.Entered), "RevertAsync never reached the suspending step");
            tweak.Resume();

            Assert.NotNull(revert);
            // A genuine ConfigureAwait(true) regression posts the continuation to the never-pumped
            // ctx, so the Task hangs and this wait fails; the fix completes it in milliseconds.
            Assert.True(await CompletesWithinAsync(revert!),
                "RevertAsync did not complete without pumping the caller's SynchronizationContext — " +
                "its gate-release continuation is pinned to the (blockable) caller thread (the Audit-2 deadlock).");
            // And prove it directly: nothing was ever posted back to the caller's context.
            Assert.Equal(0, ctx.PostCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── RecoverPendingAsync must serialize on _gate ────────────────────────────
    //
    // The startup crash-recovery sweep used to revert the leftover session and rewrite the store
    // WITHOUT holding _gate. After the user answers the "restore?" dialog the UI is live again, so a
    // Start click could run ApplyAsync concurrently with the still-running recovery revert — two
    // paths mutating the same machine-wide tweaks and doing an unsynchronized LoadStore→SaveStore,
    // which can lose the ActiveSession=null clear (resurrecting the leftover marker) or interleave
    // conflicting steps. The fix wraps RecoverPendingAsync in _gate like RevertAsync. This pins that
    // contract deterministically: while a revert is suspended HOLDING the gate, RecoverPendingAsync
    // must not proceed (it is parked on _gate.WaitAsync) — and it completes once the gate frees.

    private static string SerializePendingStore(GamingProfileStore store)
        => JsonSerializer.Serialize(store with { SchemaVersion = GamingProfileService.CurrentSchemaVersion },
            new JsonSerializerOptions { WriteIndented = true });

    [Fact]
    public async Task RecoverPendingAsync_WaitsForGate_WhenARevertHoldsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-rec-{Guid.NewGuid():N}.json");
        try
        {
            // A pending session with an ALL-FALSE profile: BuildMachineWideSteps returns an empty
            // list, so recovery's own revert is an inert no-op (no power/registry/service call) —
            // the test stays deterministic and touches nothing on the machine.
            File.WriteAllText(path, SerializePendingStore(new GamingProfileStore
            {
                ActiveSession = new GamingSessionRecord(new GamingProfile(), new GamingSnapshot()),
            }));

            var svc = StoreOnlyService(path);
            Assert.True(svc.ReadPendingRecovery().Kind == PendingRecoveryKind.LeftOn, "precondition: a leftover session is present on disk");

            // Start a revert that suspends INSIDE the gated step — RevertAsync is now holding _gate.
            var tweak = new GatedRevertTweak();
            svc.SeedAppliedStepForTest(tweak);        // IsActive → true
            var revert = svc.RevertAsync();           // acquires _gate, then parks on the step
            Assert.True(await CompletesWithinAsync(tweak.Entered),
                "RevertAsync never reached the suspending step (so it isn't holding the gate yet)");

            // With the fix, RecoverPendingAsync must block on _gate.WaitAsync while the revert holds
            // it. Deterministic assertion (no timing window): the returned Task is NOT yet completed.
            var recover = svc.RecoverPendingAsync();
            Assert.False(recover.IsCompleted,
                "RecoverPendingAsync completed while a revert held _gate — it is not serializing on the gate (the race).");

            // Release the revert; its gate-release lets the parked recovery acquire, run its (empty)
            // revert, clear the marker, and complete.
            tweak.Resume();
            Assert.True(await CompletesWithinAsync(revert), "the suspended revert did not complete after Resume");
            Assert.True(await CompletesWithinAsync(recover),
                "RecoverPendingAsync never completed after the gate was released");

            // The leftover marker is cleared exactly once, under the gate.
            Assert.True(StoreOnlyService(path).ReadPendingRecovery().Kind == PendingRecoveryKind.None,
                "the recovered session's ActiveSession marker should be cleared after recovery");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── The app-wide system-modification lock ──────────────────────────────────
    // _gate above serialises this service against ITSELF. It says nothing about Performance Mode,
    // which writes the same power plan and the same visual-effects flag through its own commands and
    // keeps its own "original" snapshot. These two tests pin the asymmetry that makes that safe:
    // APPLY refuses (nothing has changed yet, and a snapshot taken now would record the other tab's
    // change as the baseline), REVERT never refuses (a game has exited and the machine must come
    // back, even if something else holds the lock).

    [Fact]
    public async Task ApplyAsync_WhileAnotherSystemModificationRuns_IsRefusedAndChangesNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-lock-{Guid.NewGuid():N}.json");
        try
        {
            var svc = StoreOnlyService(path);

            using var held = OperationLockService.Instance.TryAcquire(
                OperationCategory.SystemModification, "Apply power plan");
            Assert.NotNull(held); // precondition: this test really is holding the lock

            // A profile with the two colliding tweaks enabled, so the test is not passing merely
            // because there was nothing to do.
            var result = await svc.ApplyAsync(
                new GamingProfile { UltimatePerformancePlan = true, DisableVisualEffects = true },
                game: null);

            Assert.Equal("Apply power plan", result.BlockedBy);
            Assert.Empty(result.Steps);
            Assert.False(result.RestorePointCreated);
            Assert.False(svc.IsActive);

            // The refusal happens BEFORE the snapshot and before the restore-point attempt, so no
            // session marker can have been written. This is what makes the test safe to run on a real
            // machine: no powercfg, no registry, no restore point.
            Assert.True(StoreOnlyService(path).ReadPendingRecovery().Kind == PendingRecoveryKind.None,
                "a refused apply must leave no crash-recovery marker behind");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RevertAsync_WhileAnotherSystemModificationRuns_StillRevertsInsteadOfStranding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm-gaming-unlock-{Guid.NewGuid():N}.json");
        try
        {
            var svc = StoreOnlyService(path);
            var log = new List<string>();
            var tweak = new FakeTweak("power plan", log);
            svc.SeedAppliedStepForTest(tweak);
            Assert.True(svc.IsActive, "precondition: a live session to revert");

            using var held = OperationLockService.Instance.TryAcquire(
                OperationCategory.SystemModification, "Apply visual effects");
            Assert.NotNull(held);

            await svc.RevertAsync();

            Assert.True(tweak.Reverted,
                "revert must run even when the lock is held: it is triggered by the game exiting, so "
                + "deferring it strands the machine on the gaming power plan with nothing left to undo it");
            Assert.False(svc.IsActive);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── The chosen game is the process that was listed (#2559) ──────────────────
    // Game mode acted on the game's ID alone. The list it was picked from can be minutes old, Windows gives a closed
    // process's ID to the next one started, and a session can last hours. Every per-game call now passes the game's
    // start time, and the auto-revert binding checks it. The CPU calls go to a substitute, so a missing check would
    // still change no real process.

    private static readonly DateTime GameStarted = new(2026, 9, 30, 8, 15, 42, DateTimeKind.Local);

    private static string NewStorePath() => Path.Combine(Path.GetTempPath(), $"sm-gaming-game-{Guid.NewGuid():N}.json");

    private static ICpuAffinityService CpuThatChangesAnything()
    {
        var cpu = Substitute.For<ICpuAffinityService>();
        cpu.TrySetPriority(Arg.Any<int>(), Arg.Any<DateTime?>(), Arg.Any<System.Diagnostics.ProcessPriorityClass>(),
                out Arg.Any<string>())
           .Returns(true);
        cpu.TrySetAffinity(Arg.Any<int>(), Arg.Any<DateTime?>(), Arg.Any<long>(), out Arg.Any<string>()).Returns(true);
        return cpu;
    }

    [Fact]
    public async Task GamePriorityTweak_PassesTheGamesStartTime_WhenItRaisesAndWhenItRestores()
    {
        var cpu = CpuThatChangesAnything();
        var tweak = new GamePriorityTweak(cpu, new GameTarget(4242, "doom.exe", GameStarted),
            System.Diagnostics.ProcessPriorityClass.Normal);

        await tweak.ApplyAsync(CancellationToken.None);
        await tweak.RevertAsync(CancellationToken.None);

        cpu.Received(1).TrySetPriority(4242, GameStarted, System.Diagnostics.ProcessPriorityClass.High, out Arg.Any<string>());
        cpu.Received(1).TrySetPriority(4242, GameStarted, System.Diagnostics.ProcessPriorityClass.Normal, out Arg.Any<string>());
    }

    [Fact]
    public async Task GameAffinityTweak_PassesTheGamesStartTime_WhenItPinsAndWhenItRestores()
    {
        var cpu = CpuThatChangesAnything();
        var tweak = new GameAffinityTweak(cpu, new GameTarget(4242, "doom.exe", GameStarted), 0b11L, 0b01L);

        await tweak.ApplyAsync(CancellationToken.None);
        await tweak.RevertAsync(CancellationToken.None);

        cpu.Received(1).TrySetAffinity(4242, GameStarted, 0b11L, out Arg.Any<string>());
        cpu.Received(1).TrySetAffinity(4242, GameStarted, 0b01L, out Arg.Any<string>());
    }

    [Fact]
    public async Task ApplyAsync_ForAGameThatHadClosed_ChangesNothing()
    {
        var path = NewStorePath();
        try
        {
            var cpu = CpuThatChangesAnything();
            cpu.HasExited(4242, GameStarted).Returns(true);
            var restorePointAttempts = 0;
            var svc = StoreOnlyService(path, (_, _) =>
            {
                restorePointAttempts++;
                return Task.FromResult(false);
            }, cpu);

            // A per-game step, which goes to the substitute, and one that needs administrator, which this service is
            // not: were the refusal missing, the batch would still change nothing on this machine.
            var result = await svc.ApplyAsync(
                new GamingProfile { HighGameCpuPriority = true, PurgeStandbyMemory = true },
                new GameTarget(4242, "doom.exe", GameStarted));

            Assert.True(result.GameClosed);
            Assert.Empty(result.Steps);
            Assert.Equal(0, restorePointAttempts);
            Assert.False(svc.IsActive);
            cpu.DidNotReceiveWithAnyArgs().TrySetPriority(default, default, default, out _);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public async Task ApplyAsync_ReadsRaisesAndRestoresTheGameAtItsStartTime()
    {
        // This test process stands in for the game, so the session stays on, watching it, until the revert. The
        // priority calls go to the substitute, so the test host's own priority is never changed.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var path = NewStorePath();
        try
        {
            var cpu = CpuThatChangesAnything();
            cpu.GetPriority(self.Id, self.StartTime).Returns(System.Diagnostics.ProcessPriorityClass.Normal);
            var svc = StoreOnlyService(path, cpu: cpu);

            var result = await svc.ApplyAsync(new GamingProfile { HighGameCpuPriority = true },
                new GameTarget(self.Id, "doom.exe", self.StartTime));
            Assert.True(svc.IsActive, "precondition: the session is on until the revert");
            await svc.RevertAsync();

            Assert.Equal(1, result.AppliedCount);
            Assert.Null(result.EndedAtStart);
            cpu.Received(1).GetPriority(self.Id, self.StartTime);
            cpu.Received(1).TrySetPriority(self.Id, self.StartTime, System.Diagnostics.ProcessPriorityClass.High,
                out Arg.Any<string>());
            cpu.Received(1).TrySetPriority(self.Id, self.StartTime, System.Diagnostics.ProcessPriorityClass.Normal,
                out Arg.Any<string>());
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public async Task ApplyAsync_WatchesTheGame_OnlyWhileItIsTheProcessThatWasListed()
    {
        // This test process stands in for the game: with its own start time it is the game, and with another it is a
        // program that has since been given the game's ID. Watching it changes nothing, and the revert lets it go.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var path = NewStorePath();
        try
        {
            var svc = StoreOnlyService(path, cpu: CpuThatChangesAnything());
            var profile = new GamingProfile { HighGameCpuPriority = true };

            await svc.ApplyAsync(profile, new GameTarget(self.Id, "game", self.StartTime.AddSeconds(-1)));
            Assert.Null(svc.BoundGamePid);
            await svc.RevertAsync();

            await svc.ApplyAsync(profile, new GameTarget(self.Id, "game", self.StartTime));
            Assert.Equal(self.Id, svc.BoundGamePid);
            await svc.RevertAsync();
            Assert.Null(svc.BoundGamePid);
        }
        finally { DeleteStore(path); }
    }

    // ── A game that closes while game mode is starting (#2563) ───────────────────
    // The watch on the game is set up last, after the restore point, the snapshot and every step, which can take many
    // seconds. A game that closed in that time left the session on, watching nothing, until Stop. The session now ends
    // before ApplyAsync returns, as the game's exit would have ended it. The timer and the CPU calls go to substitutes,
    // so neither the session nor its end changes anything on this machine.

    private static ITimerResolutionService TimerThatAccepts()
    {
        var timer = Substitute.For<ITimerResolutionService>();
        timer.Query().Returns(new TimerResolutionStatus(5000, 156250, 156250, EnabledByApp: false));
        timer.Enable().Returns(new TimerResolutionStatus(5000, 156250, 5000, EnabledByApp: true));
        return timer;
    }

    [Fact]
    public async Task ApplyAsync_WhenTheGameClosesAsGameModeStarts_EndsTheSessionAtOnce()
    {
        // The check for a closed game before any change passes, as the substitute says the game is running, and by the
        // time the watch is set up its ID names no process: int.MaxValue never does.
        var path = NewStorePath();
        try
        {
            var cpu = CpuThatChangesAnything();
            cpu.GetPriority(int.MaxValue, GameStarted).Returns(System.Diagnostics.ProcessPriorityClass.Normal);
            var timer = TimerThatAccepts();
            var svc = StoreOnlyService(path, cpu: cpu, timer: timer);

            var result = await svc.ApplyAsync(
                new GamingProfile { FinestTimerResolution = true, HighGameCpuPriority = true },
                new GameTarget(int.MaxValue, "doom.exe", GameStarted));

            Assert.Equal(2, result.AppliedCount);
            Assert.NotNull(result.EndedAtStart);
            Assert.True(result.EndedAtStart.FullyRestored);
            Assert.False(svc.IsActive);
            Assert.Null(svc.BoundGamePid);
            Assert.Equal(PendingRecoveryKind.None, svc.ReadPendingRecovery().Kind);
            timer.Received(1).Disable();
            cpu.Received(1).TrySetPriority(int.MaxValue, GameStarted, System.Diagnostics.ProcessPriorityClass.Normal,
                out Arg.Any<string>());
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public async Task ApplyAsync_WhenAnotherProcessHasTheGamesIdAsGameModeStarts_EndsTheSessionAtOnce()
    {
        // This test process stands in for a program that has been given the closed game's ID: it did not start at the
        // listed time. It is not watched, and the session ends.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var path = NewStorePath();
        try
        {
            var timer = TimerThatAccepts();
            var svc = StoreOnlyService(path, cpu: CpuThatChangesAnything(), timer: timer);

            var result = await svc.ApplyAsync(new GamingProfile { FinestTimerResolution = true },
                new GameTarget(self.Id, "game", self.StartTime.AddSeconds(-1)));

            Assert.NotNull(result.EndedAtStart);
            Assert.False(svc.IsActive);
            Assert.Null(svc.BoundGamePid);
            timer.Received(1).Disable();
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public async Task ApplyAsync_ThatChangesNothing_WatchesNothing()
    {
        // The test process is the game, and the one step fails: the substitute refuses every change. No session is on,
        // so nothing may watch the game. The next Start would not revert first, and its watch would replace this one
        // without letting it go, so this game's exit would end that session (#2563).
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var path = NewStorePath();
        try
        {
            var svc = StoreOnlyService(path, cpu: Substitute.For<ICpuAffinityService>());

            var result = await svc.ApplyAsync(new GamingProfile { HighGameCpuPriority = true },
                new GameTarget(self.Id, "game", self.StartTime));

            Assert.Equal(1, result.FailedCount);
            Assert.Null(result.EndedAtStart);
            Assert.False(svc.IsActive);
            Assert.Null(svc.BoundGamePid);
        }
        finally { DeleteStore(path); }
    }
}
