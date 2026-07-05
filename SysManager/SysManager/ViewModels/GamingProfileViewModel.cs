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
                await _service.RecoverPendingAsync();
                StatusMessage = "Reverted the leftover changes from the previous session.";
            }
        }
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        var current = SelectedGame?.ProcessId;
        var procs = await System.Threading.Tasks.Task.Run(_cpu.GetProcesses).ConfigureAwait(true);
        Processes.ReplaceWith(procs);
        // Preserve the selection across a refresh if that process is still running.
        if (current is { } pid)
            SelectedGame = Processes.FirstOrDefault(p => p.ProcessId == pid);
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

        var game = SelectedGame is { } g ? new GameTarget(g.ProcessId, g.Name) : null;
        var targetLine = game is null
            ? "No game selected — CPU affinity/priority are skipped and changes revert when you press Stop."
            : $"Optimizations apply to {game.Name} and revert automatically when it exits.";

        if (!DialogService.Instance.Confirm(
                $"Start game mode?\n\n{targetLine}\n\nEvery change is reversible from here (Stop) or automatically on game exit.",
                "Gaming Profile — Start"))
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            _service.SaveLastConfig(profile);
            var result = await _service.ApplyAsync(profile, game);
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
            await _service.RevertAsync();
            IsSessionActive = _service.IsActive;
            StatusMessage = "Game mode stopped — original settings restored.";
            ActivityLogService.Instance.Log("Gaming Profile", "Stopped game mode");
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnSessionAutoReverted(object? sender, EventArgs e)
    {
        // The bound game exited and the service auto-reverted. Reflect it in the UI (marshalled
        // to the UI thread — the event fires from a Process.Exited callback on a pool thread).
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Update()
        {
            IsSessionActive = _service.IsActive;
            StatusMessage = "The game exited — game mode ended and original settings were restored.";
        }
        if (dispatcher is null || dispatcher.CheckAccess()) Update();
        else dispatcher.Invoke(Update);
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
            System.Windows.Application.Current?.Shutdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _service.SessionAutoReverted -= OnSessionAutoReverted;
        base.Dispose(disposing);
    }
}
