// SysManager · DashboardViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly SystemInfoService _sys;
    private readonly TuneUpService _tuneUp;
    private readonly HealthScoreService _healthScore;
    private CancellationTokenSource? _tuneUpCts;

    [ObservableProperty] private SystemSnapshot? _snapshot;
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _osLine = "";
    [ObservableProperty] private string _cpuLine = "";
    [ObservableProperty] private string _memLine = "";
    [ObservableProperty] private string _diskLine = "";
    [ObservableProperty] private string _uptimeLine = "";

    // ── Health Score state ─────────────────────────────────────────────
    [ObservableProperty] private HealthScoreResult? _healthResult;
    [ObservableProperty] private bool _hasHealthScore;
    [ObservableProperty] private bool _isHealthScoreLoading;

    // ── Tune-Up state ──────────────────────────────────────────────────
    [ObservableProperty] private bool _isTuneUpRunning;
    [ObservableProperty] private string _tuneUpStep = "";
    [ObservableProperty] private int _tuneUpProgress;
    [ObservableProperty] private TuneUpResult? _tuneUpResult;
    [ObservableProperty] private bool _hasTuneUpResult;

    public DashboardViewModel(SystemInfoService sys)
        : this(sys,
            new TuneUpService(new ShortcutCleanerService(), new DiskHealthService(), sys),
            new HealthScoreService(sys, new DiskHealthService(), new BatteryService()))
    {
    }

    /// <summary>
    /// Testable constructor — accepts all dependencies explicitly.
    /// </summary>
    public DashboardViewModel(SystemInfoService sys, TuneUpService tuneUp, HealthScoreService healthScore)
    {
        _sys = sys;
        _tuneUp = tuneUp;
        _healthScore = healthScore;
        IsElevated = AdminHelper.IsElevated();
        InitializeAsync(LoadHealthScoreAsync);
    }

    // ── Health Score ───────────────────────────────────────────────────

    private async Task LoadHealthScoreAsync()
    {
        IsHealthScoreLoading = true;
        try
        {
            HealthResult = await _healthScore.ComputeAsync();
            HasHealthScore = true;
            Log.Information("Health Score computed: {Score}", HealthResult.Score);
        }
        catch (System.Management.ManagementException ex)
        {
            Log.Warning("Health Score failed: {Error}", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("Health Score failed: {Error}", ex.Message);
        }
        finally
        {
            IsHealthScoreLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning system...";
        try
        {
            Snapshot = await _sys.CaptureAsync();
            OsLine = $"{Snapshot.Os.Caption} ({Snapshot.Os.Architecture}) build {Snapshot.Os.BuildNumber}";
            CpuLine = $"{Snapshot.Cpu.Name} — {Snapshot.Cpu.Cores} cores / {Snapshot.Cpu.LogicalProcessors} threads @ {Snapshot.Cpu.MaxClockMHz} MHz — load {Snapshot.Cpu.LoadPercent:0}%";
            MemLine = $"{Snapshot.Memory.UsedGB:0.0} / {Snapshot.Memory.TotalGB:0.0} GB ({Snapshot.Memory.UsedPercent:0}%)";
            DiskLine = string.Join(" | ", Snapshot.Disks.Select(d => $"{d.FriendlyName} {d.SizeGB:0}GB {d.MediaType} {d.HealthStatus}"));
            UptimeLine = $"Uptime: {Snapshot.Os.Uptime.Days}d {Snapshot.Os.Uptime.Hours}h {Snapshot.Os.Uptime.Minutes}m";
            StatusMessage = $"Last scan: {Snapshot.CapturedAt:HH:mm:ss}";
            Log.Information("Dashboard scan completed");

            // Refresh health score too
            await LoadHealthScoreAsync();
        }
        catch (System.Management.ManagementException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void RequestElevation()
    {
        Log.Information("Admin elevation requested from Dashboard");
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    // ── Tune-Up commands ───────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunTuneUp))]
    private async Task RunTuneUpAsync()
    {
        // Confirm Recycle Bin emptying before starting
        bool emptyBin = DialogService.Instance.Confirm(
            "Quick Tune-Up will clean temp files and scan your system.\n\n" +
            "Do you also want to empty the Recycle Bin?",
            "Quick Tune-Up");

        var opLock = OperationLockService.Instance.TryAcquire(
            OperationCategory.Disk, "Quick Tune-Up");
        if (opLock == null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }

        using var opLockGuard = opLock;
        _tuneUpCts = new CancellationTokenSource();
        IsTuneUpRunning = true;
        HasTuneUpResult = false;
        TuneUpResult = null;
        TuneUpProgress = 0;
        RunTuneUpCommand.NotifyCanExecuteChanged();

        var progress = new Progress<(int Step, string Message)>(p =>
        {
            TuneUpStep = p.Message;
            // 6 steps total (0-5), map to 0-100
            TuneUpProgress = Math.Min((p.Step + 1) * 100 / 6, 100);
        });

        try
        {
            TuneUpResult = await _tuneUp.RunAsync(emptyBin, progress, _tuneUpCts.Token);
            HasTuneUpResult = true;
            StatusMessage = $"Tune-Up complete — {TuneUpResult.FreedDisplay} freed, {TuneUpResult.OverallVerdict}";
            Log.Information("Quick Tune-Up completed: {Freed} freed, {Warnings} warnings",
                TuneUpResult.FreedDisplay, TuneUpResult.WarningCount);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Tune-Up cancelled.";
        }
        catch (IOException ex)
        {
            StatusMessage = $"Tune-Up error: {ex.Message}";
            Log.Warning("TuneUp failed: {Error}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"Tune-Up error: {ex.Message}";
            Log.Warning("TuneUp failed: {Error}", ex.Message);
        }
        finally
        {
            IsTuneUpRunning = false;
            _tuneUpCts?.Dispose();
            _tuneUpCts = null;
            RunTuneUpCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunTuneUp() => !IsTuneUpRunning;

    [RelayCommand]
    private void CancelTuneUp()
    {
        _tuneUpCts?.Cancel();
    }

    [RelayCommand]
    private void DismissTuneUpResult()
    {
        HasTuneUpResult = false;
        TuneUpResult = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tuneUpCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
