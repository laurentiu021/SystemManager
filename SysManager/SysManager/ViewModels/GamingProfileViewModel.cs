// SysManager · GamingProfileViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Gaming Profile tab — a one-click "game mode" that applies a bundle of reversible
/// optimizations and reverts them automatically when the chosen game exits (or on demand).
/// Pure orchestration over <see cref="IGamingProfileService"/>; the VM only gathers the
/// desired toggles + an optional game target and reports the outcome honestly.
///
/// <para>Preview scope (v1): every action is fully reversible. Killing background processes
/// and saved multi-game profiles are intentionally not part of this preview — see the banner.</para>
/// </summary>
public sealed partial class GamingProfileViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshProcessesCommand;

    private readonly IGamingProfileService _service;
    private readonly ICpuAffinityService _cpu;

    /// <summary>Running processes the user can pick as the game to optimize (optional).</summary>
    public BulkObservableCollection<RunningProcess> Processes { get; } = new();

    [ObservableProperty] private bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGame))]
    private RunningProcess? _selectedGame;

    /// <summary>True when a game target is chosen — gates the per-game optimization toggles.</summary>
    public bool HasSelectedGame => SelectedGame is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _isSessionActive;

    // ── Profile toggles (bound to the UI; seeded from the last-used config) ──
    [ObservableProperty] private bool _ultimatePerformancePlan;
    [ObservableProperty] private bool _disableVisualEffects;
    [ObservableProperty] private bool _finestTimerResolution;
    [ObservableProperty] private bool _highGameCpuPriority;
    [ObservableProperty] private bool _pinGameToPerformanceCores;
    [ObservableProperty] private bool _purgeStandbyMemory;
    [ObservableProperty] private bool _pauseSearchIndexing;
    [ObservableProperty] private bool _silenceNotifications;

    public GamingProfileViewModel(IGamingProfileService service, ICpuAffinityService cpu)
    {
        _service = service;
        _cpu = cpu;
        IsElevated = AdminHelper.IsElevated();
        _service.SessionAutoReverted += OnSessionAutoReverted;

        LoadConfig(_service.LoadLastConfig());
        IsSessionActive = _service.IsActive;

        StatusMessage = "Pick your optimizations (optionally target a game), then Start game mode. Everything reverts on game exit or Stop.";
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        await RefreshProcessesAsync();

        // Crash recovery: a previous run may have closed/crashed with tweaks still applied.
        // Offer to revert the leftover machine-wide changes (per-game affinity/priority are
        // never persisted, so a recycled PID is never touched).
        if (_service.HasPendingRecovery)
        {
            bool revert = DialogService.Instance.Confirm(
                "SysManager closed while game mode was still active last time.\n\n" +
                "Revert the leftover system changes (power plan, visual effects, search indexing, notifications) now?",
                "Gaming Profile — Restore");
            if (revert)
            {
                var result = await _service.RecoverPendingAsync();
                StatusMessage = DescribeRevert(result,
                    "Reverted the leftover changes from the previous session.",
                    "Reverted the leftover changes from the previous session");
            }
        }
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        var current = SelectedGame;
        var procs = await System.Threading.Tasks.Task.Run(_cpu.GetProcesses).ConfigureAwait(true);
        Processes.ReplaceWith(procs);
        // Preserve the selection across a refresh if that process is still running. The start time is what tells it
        // from a new process Windows has given the same ID (#2559).
        if (current is not null)
            SelectedGame = Processes.FirstOrDefault(
                p => p.ProcessId == current.ProcessId && p.StartTime == current.StartTime);
    }

    /// <summary>True when nothing is applied yet and at least one optimization is ticked.</summary>
    public bool CanApply => !IsSessionActive && BuildProfile().HasAnyEnabled;

    private GamingProfile BuildProfile() => new()
    {
        UltimatePerformancePlan = UltimatePerformancePlan,
        DisableVisualEffects = DisableVisualEffects,
        FinestTimerResolution = FinestTimerResolution,
        HighGameCpuPriority = HighGameCpuPriority,
        PinGameToPerformanceCores = PinGameToPerformanceCores,
        PurgeStandbyMemory = PurgeStandbyMemory,
        PauseSearchIndexing = PauseSearchIndexing,
        SilenceNotifications = SilenceNotifications,
    };

    private void LoadConfig(GamingProfile p)
    {
        UltimatePerformancePlan = p.UltimatePerformancePlan;
        DisableVisualEffects = p.DisableVisualEffects;
        FinestTimerResolution = p.FinestTimerResolution;
        HighGameCpuPriority = p.HighGameCpuPriority;
        PinGameToPerformanceCores = p.PinGameToPerformanceCores;
        PurgeStandbyMemory = p.PurgeStandbyMemory;
        PauseSearchIndexing = p.PauseSearchIndexing;
        SilenceNotifications = p.SilenceNotifications;
    }

    // Any toggle change re-evaluates whether Start is enabled + which per-game toggles apply.
    partial void OnUltimatePerformancePlanChanged(bool value) => OnAnyToggleChanged();
    partial void OnDisableVisualEffectsChanged(bool value) => OnAnyToggleChanged();
    partial void OnFinestTimerResolutionChanged(bool value) => OnAnyToggleChanged();
    partial void OnHighGameCpuPriorityChanged(bool value) => OnAnyToggleChanged();
    partial void OnPinGameToPerformanceCoresChanged(bool value) => OnAnyToggleChanged();
    partial void OnPurgeStandbyMemoryChanged(bool value) => OnAnyToggleChanged();
    partial void OnPauseSearchIndexingChanged(bool value) => OnAnyToggleChanged();
    partial void OnSilenceNotificationsChanged(bool value) => OnAnyToggleChanged();

    private void OnAnyToggleChanged()
    {
        OnPropertyChanged(nameof(CanApply));
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => CanApply;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var profile = BuildProfile();
        if (!profile.HasAnyEnabled)
        {
            StatusMessage = "Tick at least one optimization first.";
            return;
        }

        var game = SelectedGame is { } g ? new GameTarget(g.ProcessId, g.Name, g.StartTime) : null;
        var targetLine = game is null
            ? "No game selected — CPU affinity/priority are skipped and changes revert when you press Stop."
            : $"Optimizations apply to {game.Name} and revert automatically when it exits.";

        if (!DialogService.Instance.Confirm(
                $"Start game mode?\n\n{targetLine}\n\nEvery change is reversible from here (Stop) or automatically on game exit." +
                _service.RestorePointNotice,
                "Gaming Profile — Start"))
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            _service.SaveLastConfig(profile);
            var result = await _service.ApplyAsync(profile, game);
            if (result.BlockedBy is { } blocker)
            {
                // Nothing was applied and no snapshot was taken, so say so and stop — do not log an
                // activity entry or flip IsSessionActive for a session that never began. Same wording
                // Performance Mode uses when the lock is held, because these two tabs are the pair
                // that collide: they write the same power plan and the same visual-effects flag.
                StatusMessage = $"Cannot start — {blocker} is already running.";
                return;
            }

            if (result.StoreUnreadable)
            {
                // Nothing was applied either. The record it could not read is the one that undoes game mode
                // when SysManager closes during a game, so it must not be written over (#2521).
                StatusMessage = "Cannot start — SysManager could not read the record it keeps to undo game mode "
                    + "if it closes during a game. Try again in a moment.";
                return;
            }

            if (result.GameClosed && game is not null)
            {
                // Nothing was applied: the game closed after the list was read, and its ID may belong to another
                // program by now (#2559). The refresh takes it off the list, and the selection with it.
                await RefreshProcessesAsync();
                StatusMessage = $"{game.Name} had already closed, so game mode did not start. "
                    + "Pick the game again from the refreshed list.";
                return;
            }

            if (result.EndedAtStart is { } undone && game is not null)
            {
                // The game closed while game mode was starting, and the service ended the session as the game's exit
                // would have (#2563), so no session is on, as before Start. The refresh takes the game off the list,
                // and the selection with it.
                await RefreshProcessesAsync();
                StatusMessage = DescribeRevert(undone,
                    $"{game.Name} closed while game mode was starting, so game mode ended and original settings were restored.",
                    $"{game.Name} closed while game mode was starting, so game mode ended");
                return;
            }

            IsSessionActive = _service.IsActive;
            StatusMessage = DescribeResult(result, game);
            ActivityLogService.Instance.Log("Gaming Profile",
                $"Started game mode ({result.AppliedCount} optimization(s))");
            Log.Information("Gaming Profile started: {Applied} applied, {Admin} need admin, {Failed} failed",
                result.AppliedCount, result.SkippedForAdminCount, result.FailedCount);
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    private bool CanStop() => IsSessionActive;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (!DialogService.Instance.Confirm(
                "Stop game mode and restore all changed settings to how they were before?",
                "Gaming Profile — Stop"))
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var result = await _service.RevertAsync();
            IsSessionActive = _service.IsActive;
            StatusMessage = DescribeRevert(result,
                "Game mode stopped — original settings restored.",
                "Game mode stopped");
            ActivityLogService.Instance.Log("Gaming Profile", "Stopped game mode");
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnSessionAutoReverted(object? sender, GamingRevertResult result)
    {
        // The bound game exited and the service auto-reverted. Reflect it in the UI (marshalled
        // to the UI thread — the event fires from a Process.Exited callback on a pool thread).
        UiThread.Post(() =>
        {
            IsSessionActive = _service.IsActive;
            StatusMessage = DescribeRevert(result,
                "The game exited — game mode ended and original settings were restored.",
                "The game exited and game mode ended");
        });
    }

    /// <summary>
    /// The status line after a revert: <paramref name="fullyRestored"/> when every step came back, otherwise
    /// <paramref name="partialLead"/> followed by the settings that did not, so a failed undo is never announced
    /// as a restore (#2445).
    /// </summary>
    internal static string DescribeRevert(GamingRevertResult result, string fullyRestored, string partialLead)
    {
        if (result.FullyRestored) return fullyRestored;
        var one = result.NotRestored.Count == 1;
        var what = one
            ? $"\"{result.NotRestored[0]}\" was"
            : $"{result.NotRestored.Count} settings were ({string.Join(", ", result.NotRestored)})";
        return $"{partialLead}, but {what} not restored — check {(one ? "it" : "them")} yourself. The log has the reason.";
    }

    /// <summary>Builds an honest, plain-language summary of an apply batch (pure, testable).</summary>
    internal static string DescribeResult(GamingApplyResult result, GameTarget? game)
    {
        var parts = new List<string> { $"Game mode on — {result.AppliedCount} optimization(s) applied" };
        if (game is not null) parts[0] += $" for {game.Name}";
        if (result.SkippedForAdminCount > 0)
            parts.Add($"{result.SkippedForAdminCount} need administrator (run as admin and retry)");
        if (result.FailedCount > 0)
            parts.Add($"{result.FailedCount} could not be applied");
        if (result.RestorePointCreated)
            parts.Add("restore point created");
        return string.Join(" · ", parts) + ".";
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _service.SessionAutoReverted -= OnSessionAutoReverted;
        base.Dispose(disposing);
    }
}
