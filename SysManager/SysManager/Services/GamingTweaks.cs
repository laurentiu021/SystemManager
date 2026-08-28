// SysManager · GamingTweaks — the concrete reversible steps composed by GamingProfileService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

// Each step here composes an ALREADY-AUDITED SysManager service and restores an injected
// original on revert. None reimplements the underlying tweak. They are internal because the
// engine (GamingProfileService) is the only composer; tests exercise the engine with fakes.

/// <summary>Switch to the Ultimate Performance power plan; restore the original plan GUID on revert.</summary>
internal sealed class PowerPlanTweak(PerformanceService performance, string? originalPlanGuid) : IGamingTweak
{
    public string Label => "Ultimate Performance power plan";
    public bool RequiresAdmin => false;

    public async Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
    {
        var guid = await performance.EnsureUltimatePerformancePlanAsync(ct).ConfigureAwait(false);
        // Couldn't find/create the Ultimate Performance scheme — a genuine non-fatal failure.
        if (string.IsNullOrEmpty(guid)) return GamingTweakResult.Failed;
        await performance.SetActivePlanAsync(guid, ct).ConfigureAwait(false);
        return GamingTweakResult.Applied;
    }

    public async Task RevertAsync(CancellationToken ct)
    {
        // The engine never builds this step without a captured original (see BuildMachineWideSteps),
        // but stay defensive: restore nothing if the original is unknown.
        if (string.IsNullOrEmpty(originalPlanGuid)) return;
        await performance.SetActivePlanAsync(originalPlanGuid, ct).ConfigureAwait(false);
    }
}

/// <summary>Turn off UI visual effects; restore whether they were enabled on revert.</summary>
internal sealed class VisualEffectsTweak(bool originalEnabled) : IGamingTweak
{
    public string Label => "Reduce visual effects";
    public bool RequiresAdmin => false;

    public Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
    {
        // Already reduced → no-op (nothing to change, nothing to restore beyond the captured state).
        if (!originalEnabled) return Task.FromResult(GamingTweakResult.NoChange);
        PerformanceService.SetUiEffects(false);
        return Task.FromResult(GamingTweakResult.Applied);
    }

    public Task RevertAsync(CancellationToken ct)
    {
        PerformanceService.SetUiEffects(originalEnabled);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Request the finest multimedia timer resolution while the game runs; release it on revert.
/// A per-process request Windows also releases automatically when SysManager exits.
/// </summary>
internal sealed class TimerResolutionTweak(ITimerResolutionService timer) : IGamingTweak
{
    public string Label => "Finest timer resolution (~0.5 ms)";
    public bool RequiresAdmin => false;

    public Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
    {
        timer.Enable();
        return Task.FromResult(GamingTweakResult.Applied);
    }

    public Task RevertAsync(CancellationToken ct)
    {
        timer.Disable();
        return Task.CompletedTask;
    }
}

/// <summary>Raise the game process's CPU priority to High; restore the original class on revert.</summary>
internal sealed class GamePriorityTweak(ICpuAffinityService cpu, int gamePid, ProcessPriorityClass? original) : IGamingTweak
{
    public string Label => "High game CPU priority";
    public bool RequiresAdmin => false;

    public Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
        => Task.FromResult(cpu.TrySetPriority(gamePid, ProcessPriorityClass.High, out _)
            ? GamingTweakResult.Applied
            : GamingTweakResult.Failed);

    public Task RevertAsync(CancellationToken ct)
    {
        if (original is { } p) cpu.TrySetPriority(gamePid, p, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Pin the game process to the performance cores (all cores on a non-hybrid CPU); restore the
/// original affinity mask on revert. Affinity also self-clears when the process exits.
/// </summary>
internal sealed class GameAffinityTweak(ICpuAffinityService cpu, int gamePid, long targetMask, long? originalMask) : IGamingTweak
{
    public string Label => "Pin game to performance cores";
    public bool RequiresAdmin => false;

    public Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
    {
        // No performance cores to target (unknown topology) → benign no-op, not a failure.
        if (targetMask == 0) return Task.FromResult(GamingTweakResult.NoChange);
        return Task.FromResult(cpu.TrySetAffinity(gamePid, targetMask, out _)
            ? GamingTweakResult.Applied
            : GamingTweakResult.Failed);
    }

    public Task RevertAsync(CancellationToken ct)
    {
        if (originalMask is { } m && m != 0) cpu.TrySetAffinity(gamePid, m, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Purge the Windows standby list to free cached RAM. One-shot and non-destructive — there is
/// nothing to revert (cached data is simply re-read from disk on next use). Needs admin.
/// </summary>
internal sealed class StandbyPurgeTweak(StandbyMemoryService standby) : IGamingTweak
{
    public string Label => "Free standby memory";
    public bool RequiresAdmin => true;

    public Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
        // A successful purge IS a real action (RAM freed) so it counts as Applied and is shown to
        // the user; it simply has no revert counterpart (its RevertAsync below is a no-op).
        => Task.FromResult(standby.TryPurgeStandbyList(out _)
            ? GamingTweakResult.Applied
            : GamingTweakResult.Failed);

    public Task RevertAsync(CancellationToken ct) => Task.CompletedTask; // one-shot, nothing to undo
}

/// <summary>
/// Temporarily STOP (not disable) the Windows Search indexer to cut background disk/CPU;
/// restart it on revert only if it was running before. Needs admin.
/// </summary>
internal sealed class SearchIndexingTweak(bool wasRunning) : IGamingTweak
{
    public const string ServiceName = "WSearch";

    public string Label => "Pause search indexing";
    public bool RequiresAdmin => true;

    public async Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
    {
        // The indexer was already stopped before apply → nothing to stop, nothing to restart on
        // revert. A no-op keeps us from restarting a service the user had intentionally stopped.
        if (!wasRunning) return GamingTweakResult.NoChange;
        await ServiceManagerService.StopServiceAsync(ServiceName).ConfigureAwait(false);
        return GamingTweakResult.Applied;
    }

    public async Task RevertAsync(CancellationToken ct)
    {
        // Only restart what we stopped: if the indexer was already stopped before apply,
        // leave it stopped (restoring the exact prior state).
        if (wasRunning)
            await ServiceManagerService.StartServiceAsync(ServiceName).ConfigureAwait(false);
    }
}

/// <summary>
/// Silence toast notifications via the documented HKCU push-notifications key (reversible, no
/// admin). This mutes toasts while gaming; it is NOT the Focus Assist / Do-Not-Disturb tile
/// (no stable public API), which the UI copy states plainly. Restores the original DWORD on
/// revert (deleting the value if it was absent, to restore the exact prior state) — but only
/// while the value is still the 0 this tweak wrote. The key is shared with
/// <see cref="NotificationBlockerService"/>, which owns the path and value name, so the user can
/// move the same switch from the Notifications tab while a profile is active; if they have, revert
/// keeps their choice rather than resurrecting the pre-game state.
/// </summary>
internal sealed class NotificationsTweak(
    int? originalToastEnabled,
    RegistryKey? baseKey = null,
    int? writeCountAtApply = null,
    string? configDir = null) : IGamingTweak
{
    // The key path and value name are NOT redeclared here. They are owned by
    // NotificationBlockerService, whose Notifications tab writes the same user-wide master toggle,
    // and referenced from there so the two cannot drift — the situation Helpers/WingetId.cs exists
    // to prevent, where the same rule lived in three services and could diverge in one of them.
    // ToastEnabled = 0 suppresses toasts; absent/1 = normal.
    //
    // The registry root is injectable (defaulting to HKCU) for the same reason as
    // NotificationBlockerService and AppBlockerService: it lets the apply/revert round-trip be
    // tested against a redirected subkey instead of the machine's real notification settings.
    private RegistryKey Root => baseKey ?? Registry.CurrentUser;

    public string Label => "Silence notifications";
    public bool RequiresAdmin => false;

    /// <summary>Reads the current ToastEnabled DWORD (null = value absent → notifications on).</summary>
    internal static int? ReadToastEnabled(RegistryKey? baseKey = null)
    {
        try
        {
            using var key = (baseKey ?? Registry.CurrentUser)
                .OpenSubKey(NotificationBlockerService.PushKeyPath, writable: false);
            return key?.GetValue(NotificationBlockerService.ToastValueName) is int i ? i : null;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
    {
        // Toasts already suppressed (ToastEnabled == 0) → no-op; nothing to change or restore.
        if (originalToastEnabled == 0) return Task.FromResult(GamingTweakResult.NoChange);
        using var key = Root.CreateSubKey(NotificationBlockerService.PushKeyPath, writable: true);
        key.SetValue(NotificationBlockerService.ToastValueName, 0, RegistryValueKind.DWord);
        Log.Information("Gaming Profile: notifications silenced (ToastEnabled=0)");
        return Task.FromResult(GamingTweakResult.Applied);
    }

    public Task RevertAsync(CancellationToken ct)
    {
        // Undo only the value this tweak actually wrote. A NoChange apply is never tracked for
        // revert, so reaching here means ToastEnabled was set to 0 by us; if it is no longer 0 the
        // user has since changed the master toggle themselves — most likely on the Notifications
        // tab, which writes this very value — and restoring the pre-game snapshot would silently
        // overturn that newer decision. Leaving it alone is what "reversible" has to mean when the
        // user has already moved the switch.
        var current = ReadToastEnabled(baseKey);
        if (current != 0)
        {
            Log.Information(
                "Gaming Profile: notifications master toggle was changed while the profile was active "
                + "(now {Current}); leaving the user's setting instead of restoring {Original}",
                current, originalToastEnabled);
            return Task.CompletedTask;
        }

        // The value IS 0 — but the guard above can only catch the user switching notifications back ON.
        // The opposite order is invisible to it: if the user muted notifications themselves while the
        // profile was active, the registry holds a 0 that is byte-identical to the one this tweak wrote, so
        // restoring the snapshot would silently re-enable notifications they had just deliberately turned
        // off. Nothing in the registry can attribute the write, which is why the Notifications tab keeps a
        // write ledger and this compares against the count captured at apply.
        var writesNow = NotificationBlockerService.ReadMasterToggleWriteCount(configDir);
        if (writeCountAtApply is { } atApply && writesNow != atApply)
        {
            Log.Information(
                "Gaming Profile: the user set the notifications master toggle themselves while the profile "
                + "was active (ledger {AtApply} -> {Now}); leaving it muted instead of restoring {Original}",
                atApply, writesNow, originalToastEnabled);
            return Task.CompletedTask;
        }

        using var key = Root.CreateSubKey(NotificationBlockerService.PushKeyPath, writable: true);
        if (originalToastEnabled is { } v)
            key.SetValue(NotificationBlockerService.ToastValueName, v, RegistryValueKind.DWord);
        else
            key.DeleteValue(NotificationBlockerService.ToastValueName, throwOnMissingValue: false); // was absent → restore absent
        return Task.CompletedTask;
    }
}
