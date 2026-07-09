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
/// revert (deleting the value if it was absent, to restore the exact prior state).
/// </summary>
internal sealed class NotificationsTweak(int? originalToastEnabled) : IGamingTweak
{
    // Per-user master toggle for toast notifications (the same value the Settings > Notifications
    // switch writes). ToastEnabled = 0 suppresses toasts; absent/1 = normal.
    internal const string PushKeyPath = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    internal const string ToastValueName = "ToastEnabled";

    public string Label => "Silence notifications";
    public bool RequiresAdmin => false;

    /// <summary>Reads the current ToastEnabled DWORD (null = value absent → notifications on).</summary>
    internal static int? ReadToastEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PushKeyPath, writable: false);
            return key?.GetValue(ToastValueName) is int i ? i : null;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public Task<GamingTweakResult> ApplyAsync(CancellationToken ct)
    {
        // Toasts already suppressed (ToastEnabled == 0) → no-op; nothing to change or restore.
        if (originalToastEnabled == 0) return Task.FromResult(GamingTweakResult.NoChange);
        using var key = Registry.CurrentUser.CreateSubKey(PushKeyPath, writable: true);
        key.SetValue(ToastValueName, 0, RegistryValueKind.DWord);
        Log.Information("Gaming Profile: notifications silenced (ToastEnabled=0)");
        return Task.FromResult(GamingTweakResult.Applied);
    }

    public Task RevertAsync(CancellationToken ct)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PushKeyPath, writable: true);
        if (originalToastEnabled is { } v)
            key.SetValue(ToastValueName, v, RegistryValueKind.DWord);
        else
            key.DeleteValue(ToastValueName, throwOnMissingValue: false); // was absent → restore absent
        return Task.CompletedTask;
    }
}
