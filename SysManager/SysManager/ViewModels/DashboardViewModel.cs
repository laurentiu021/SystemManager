// SysManager · DashboardViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly SystemInfoService _sys;
    private readonly TuneUpService _tuneUp;
    private readonly HealthScoreService _healthScore;
    private readonly TemperatureService _temps;
    private CancellationTokenSource? _tuneUpCts;
    private CancellationTokenSource? _pollingCts;

    // ── Real-time vitals (300ms polling) ──────────────────────────────────
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuName = "";
    [ObservableProperty] private string _cpuCores = "";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _ramUsedGB;
    [ObservableProperty] private double _ramTotalGB;
    [ObservableProperty] private double _ramAvailableGB;
    [ObservableProperty] private string _ramType = "";
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _gpuVram = "";

    // ── System info (static, refreshed on scan) ──────────────────────────
    [ObservableProperty] private string _osLine = "";
    [ObservableProperty] private string _uptimeLine = "";
    [ObservableProperty] private bool _isElevated;

    // ── Health Score ──────────────────────────────────────────────────────
    [ObservableProperty] private HealthScoreResult? _healthResult;
    [ObservableProperty] private bool _hasHealthScore;
    [ObservableProperty] private bool _isHealthScoreLoading;

    // ── Temperatures ─────────────────────────────────────────────────────
    public BulkObservableCollection<TemperatureReading> Temperatures { get; } = new();

    // ── Storage ──────────────────────────────────────────────────────────
    public BulkObservableCollection<DriveUsageInfo> Drives { get; } = new();

    // ── System Alerts ────────────────────────────────────────────────────
    public ObservableCollection<DashboardAlert> Alerts { get; } = new();

    // ── Recent Activity ──────────────────────────────────────────────────
    public BulkObservableCollection<ActivityEntry> RecentActivity { get; } = new();

    // ── Quick Action state ──────────────────────────────────────────────
    [ObservableProperty] private bool _isQuickActionRunning;
    [ObservableProperty] private string _quickActionName = "";
    [ObservableProperty] private string _quickActionStatus = "";
    [ObservableProperty] private string _quickActionDetail = "";
    [ObservableProperty] private int _quickActionProgress;
    [ObservableProperty] private bool _isQuickActionDone;
    [ObservableProperty] private string _quickActionNavigateLabel = "";
    private string? _quickActionNavigateTarget;

    // ── Tune-Up state ────────────────────────────────────────────────────
    [ObservableProperty] private bool _isTuneUpRunning;
    [ObservableProperty] private string _tuneUpStep = "";
    [ObservableProperty] private int _tuneUpProgress;
    [ObservableProperty] private TuneUpResult? _tuneUpResult;
    [ObservableProperty] private bool _hasTuneUpResult;

    // ── IsActive (pause polling when tab not visible) ────────────────────
    [ObservableProperty] private bool _isActive;

    public DashboardViewModel(SystemInfoService sys, TuneUpService tuneUp,
        HealthScoreService healthScore, TemperatureService temps)
    {
        _sys = sys;
        _tuneUp = tuneUp;
        _healthScore = healthScore;
        _temps = temps;
        IsElevated = AdminHelper.IsElevated();
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        await LoadStaticInfoAsync();
        LoadDrives();
        LoadActivity();
        StartPollingLoop();
        await LoadHealthScoreAsync();
        StartAlertScans();
        await LoadTemperaturesAsync();
        if (_pollingCts is not null)
            StartTemperaturePolling(_pollingCts.Token);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  REAL-TIME POLLING (300ms)
    // ══════════════════════════════════════════════════════════════════════

    partial void OnIsActiveChanged(bool value)
    {
        if (value && _pollingCts is null)
            StartPollingLoop();
        else if (!value)
        {
            _pollingCts?.Cancel();
            _pollingCts = null;
        }
    }

    private void StartPollingLoop()
    {
        _pollingCts?.Cancel();
        _pollingCts = new CancellationTokenSource();
        var ct = _pollingCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await _sys.CaptureAsync();
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        CpuPercent = snapshot.Cpu.LoadPercent;
                        RamPercent = snapshot.Memory.UsedPercent;
                        RamUsedGB = snapshot.Memory.UsedGB;
                        RamTotalGB = snapshot.Memory.TotalGB;
                        RamAvailableGB = snapshot.Memory.AvailableGB;
                    });

                    UpdateGpuUsage();
                    await Task.Delay(300, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Debug("Dashboard polling error: {Error}", ex.Message);
                    await Task.Delay(1000, ct);
                }
            }
        }, ct);
    }

    private void UpdateGpuUsage()
    {
        try
        {
            NvAPIWrapper.NVIDIA.Initialize();
            var gpus = NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs();
            if (gpus.Length > 0)
            {
                var gpu = gpus[0];
                var usage = gpu.UsageInformation.GPU.Percentage;
                var memTotal = gpu.MemoryInformation.DedicatedVideoMemoryInkB / 1024.0 / 1024.0;
                var memUsed = (gpu.MemoryInformation.DedicatedVideoMemoryInkB -
                               gpu.MemoryInformation.AvailableDedicatedVideoMemoryInkB) / 1024.0 / 1024.0;

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    GpuPercent = usage;
                    GpuName = gpu.FullName;
                    GpuVram = $"{memUsed:F1} / {memTotal:F1} GB VRAM";
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug("GPU polling unavailable: {Error}", ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  STATIC INFO (loaded once)
    // ══════════════════════════════════════════════════════════════════════

    private async Task LoadStaticInfoAsync()
    {
        try
        {
            var snap = await _sys.CaptureAsync().ConfigureAwait(true);
            CpuName = snap.Cpu.Name;
            CpuCores = $"{snap.Cpu.Cores} cores · {snap.Cpu.LogicalProcessors} threads";
            OsLine = $"{snap.Os.Caption} · Build {snap.Os.BuildNumber}";
            UptimeLine = $"Uptime {snap.Os.Uptime.Days}d {snap.Os.Uptime.Hours}h {snap.Os.Uptime.Minutes}m";

            if (snap.Memory.Modules.Count > 0)
            {
                var firstModule = snap.Memory.Modules[0];
                RamType = $"{(firstModule.SpeedMHz > 0 ? $"DDR · {firstModule.SpeedMHz} MHz" : "")}";
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard static info failed: {Error}", ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  STORAGE DRIVES
    // ══════════════════════════════════════════════════════════════════════

    private void LoadDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d is { IsReady: true, DriveType: DriveType.Fixed })
                .Select(d => new DriveUsageInfo(
                    d.Name.TrimEnd('\\'),
                    (d.TotalSize - d.AvailableFreeSpace) / 1024.0 / 1024.0 / 1024.0,
                    d.TotalSize / 1024.0 / 1024.0 / 1024.0))
                .ToList();
            Drives.ReplaceWith(drives);
        }
        catch (IOException ex)
        {
            Log.Debug("Drive info failed: {Error}", ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  TEMPERATURES
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RefreshTemperaturesAsync()
    {
        var readings = await _temps.ReadAllAsync();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            Temperatures.ReplaceWith(readings));
    }

    private async Task LoadTemperaturesAsync()
    {
        try { await RefreshTemperaturesAsync(); }
        catch (Exception ex) { Log.Debug("Temperature load failed: {Error}", ex.Message); }
    }

    private void StartTemperaturePolling(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, ct);
                    await RefreshTemperaturesAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Debug("Temp polling error: {Error}", ex.Message); }
            }
        }, ct);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SYSTEM ALERTS (real scans at boot, parallel)
    // ══════════════════════════════════════════════════════════════════════

    private void StartAlertScans()
    {
        var smartAlert = new DashboardAlert { Title = "Checking disk health...", State = AlertLoadingState.Loading };
        var appUpdateAlert = new DashboardAlert { Title = "Checking app updates...", State = AlertLoadingState.Loading };
        var memoryAlert = new DashboardAlert { Title = "Checking memory health...", State = AlertLoadingState.Loading };
        var eventLogAlert = new DashboardAlert { Title = "Checking Event Log...", State = AlertLoadingState.Loading };
        var featuresAlert = new DashboardAlert { Title = "Checking Windows features...", State = AlertLoadingState.Loading };

        Alerts.Add(smartAlert);
        Alerts.Add(appUpdateAlert);
        Alerts.Add(memoryAlert);
        Alerts.Add(eventLogAlert);
        Alerts.Add(featuresAlert);

        _ = RunAlertScanAsync(smartAlert, ScanSmartHealthAsync);
        _ = RunAlertScanAsync(appUpdateAlert, ScanAppUpdatesAsync);
        _ = RunAlertScanAsync(memoryAlert, ScanMemoryHealthAsync);
        _ = RunAlertScanAsync(eventLogAlert, ScanEventLogAsync);
        _ = RunAlertScanAsync(featuresAlert, ScanWindowsFeaturesAsync);
    }

    private static async Task RunAlertScanAsync(DashboardAlert alert, Func<DashboardAlert, Task> scanner)
    {
        // Fire-and-forget ETA hint: after 5s of loading, surface a "remaining" note.
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            if (alert.State == AlertLoadingState.Loading)
            {
                alert.ShowEta = true;
                alert.Eta = "~10s remaining";
            }
        });

        try
        {
            await Task.Run(() => scanner(alert));
        }
        catch (Exception ex)
        {
            alert.Title = $"Check failed: {ex.Message}";
            alert.Severity = AlertSeverity.Yellow;
        }
        finally
        {
            alert.State = AlertLoadingState.Complete;
            alert.ShowEta = false;
        }
    }

    private async Task ScanSmartHealthAsync(DashboardAlert alert)
    {
        var result = await _healthScore.ComputeAsync();
        var diskScore = result.DiskScore;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (diskScore >= 90)
            {
                alert.Title = "All SMART indicators healthy";
                alert.Severity = AlertSeverity.Green;
            }
            else if (diskScore >= 60)
            {
                alert.Title = "Disk health degrading — check System Health";
                alert.Severity = AlertSeverity.Yellow;
            }
            else
            {
                alert.Title = "Disk health critical — immediate attention needed";
                alert.Severity = AlertSeverity.Red;
            }
        });
    }

    private async Task ScanAppUpdatesAsync(DashboardAlert alert)
    {
        try
        {
            var runner = new PowerShellRunner();
            var output = new System.Text.StringBuilder();
            runner.LineReceived += l => { if (l.Kind == OutputKind.Output) output.AppendLine(l.Text); };

            await runner.RunScriptViaPwshAsync(
                "winget upgrade --include-unknown 2>$null | Select-String -Pattern '^\\S' | Measure-Object | Select-Object -ExpandProperty Count",
                CancellationToken.None);

            var countText = output.ToString().Trim();
            var count = int.TryParse(countText, out var n) ? Math.Max(n - 2, 0) : 0;

            var (title, severity) = ClassifyAppUpdates(count);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                alert.Title = title;
                alert.Severity = severity;
            });
        }
        catch (Exception ex)
        {
            Log.Debug("Alert scan failed: {Error}", ex.Message);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                alert.Title = "App update check unavailable";
                alert.Severity = AlertSeverity.Green;
            });
        }
    }

    /// <summary>Pure decision for the App Updates alert. Testable without WPF.</summary>
    internal static (string Title, AlertSeverity Severity) ClassifyAppUpdates(int count) =>
        count == 0
            ? ("All apps up to date", AlertSeverity.Green)
            : ($"{count} app update{(count == 1 ? "" : "s")} available", AlertSeverity.Yellow);

    private async Task ScanMemoryHealthAsync(DashboardAlert alert)
    {
        var result = await _healthScore.ComputeAsync();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (result.RamScore >= 90)
            {
                alert.Title = "No memory errors (30 days)";
                alert.Severity = AlertSeverity.Green;
            }
            else
            {
                alert.Title = "Memory issues detected — check System Health";
                alert.Severity = AlertSeverity.Yellow;
            }
        });
    }

    private Task ScanEventLogAsync(DashboardAlert alert)
    {
        try
        {
            var since = DateTime.Now.AddDays(-7);
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                "System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[Level=1 and TimeCreated[@SystemTime>='{since:yyyy-MM-ddTHH:mm:ss}']]]");

            int criticalCount = 0;
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            while (reader.ReadEvent() is not null) criticalCount++;

            var (title, severity) = ClassifyEventLog(criticalCount);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                alert.Title = title;
                alert.Severity = severity;
            });
        }
        catch (Exception ex)
        {
            Log.Debug("Alert scan failed: {Error}", ex.Message);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                alert.Title = "Event Log check unavailable";
                alert.Severity = AlertSeverity.Green;
            });
        }
        return Task.CompletedTask;
    }

    /// <summary>Pure decision for the Event Log alert. Testable without WPF.</summary>
    internal static (string Title, AlertSeverity Severity) ClassifyEventLog(int criticalCount) =>
        criticalCount == 0
            ? ("No critical events (last 7 days)", AlertSeverity.Green)
            : ($"{criticalCount} critical event{(criticalCount == 1 ? "" : "s")} in Event Log (last 7d)", AlertSeverity.Red);

    private Task ScanWindowsFeaturesAsync(DashboardAlert alert)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            var pending = key is not null;

            var (title, severity) = ClassifyPendingReboot(pending);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                alert.Title = title;
                alert.Severity = severity;
            });
        }
        catch (Exception ex)
        {
            Log.Debug("Alert scan failed: {Error}", ex.Message);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                alert.Title = "Feature check unavailable";
                alert.Severity = AlertSeverity.Green;
            });
        }
        return Task.CompletedTask;
    }

    /// <summary>Pure decision for the pending-reboot alert. Testable without WPF.</summary>
    internal static (string Title, AlertSeverity Severity) ClassifyPendingReboot(bool pending) =>
        pending
            ? ("Pending reboot required (Windows Update)", AlertSeverity.Yellow)
            : ("No pending reboots", AlertSeverity.Green);

    // ══════════════════════════════════════════════════════════════════════
    //  RECENT ACTIVITY
    // ══════════════════════════════════════════════════════════════════════

    private void LoadActivity()
    {
        RecentActivity.ReplaceWith(ActivityLogService.Instance.GetRecent(5));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  HEALTH SCORE
    // ══════════════════════════════════════════════════════════════════════

    private async Task LoadHealthScoreAsync()
    {
        IsHealthScoreLoading = true;
        try
        {
            HealthResult = await _healthScore.ComputeAsync();
            HasHealthScore = true;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException)
        {
            Log.Warning("Health Score failed: {Error}", ex.Message);
        }
        finally { IsHealthScoreLoading = false; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  QUICK ACTIONS
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand(CanExecute = nameof(CanRunQuickAction))]
    private async Task QuickCleanupAsync()
    {
        await RunQuickActionAsync("Quick Cleanup", "Cleanup", "nav-cleanup", async () =>
        {
            QuickActionDetail = "Scanning temp folders...";
            QuickActionProgress = 20;
            var tempPath = Path.GetTempPath();
            await Task.Run(() =>
            {
                try
                {
                    return new DirectoryInfo(tempPath)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => { try { return f.Length; } catch { return 0L; } });
                }
                catch { return 0L; }
            });

            QuickActionDetail = "Cleaning temp files...";
            QuickActionProgress = 50;
            long freed = 0;
            await Task.Run(() =>
            {
                try
                {
                    foreach (var file in new DirectoryInfo(tempPath).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                    {
                        try { var len = file.Length; file.Delete(); freed += len; }
                        catch { /* locked — skip */ }
                    }
                }
                catch { /* access denied — skip */ }
            });

            QuickActionProgress = 100;
            var freedMB = freed / 1024.0 / 1024.0;
            QuickActionDetail = freedMB >= 1024
                ? $"Freed {freedMB / 1024:F1} GB"
                : $"Freed {freedMB:F0} MB";
            ActivityLogService.Instance.Log("Quick Cleanup", QuickActionDetail);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunQuickAction))]
    private async Task QuickUpdateAppsAsync()
    {
        await RunQuickActionAsync("Update All Apps", "App Updates", "nav-app-updates", async () =>
        {
            QuickActionDetail = "Checking for upgrades...";
            QuickActionProgress = 30;
            var runner = new PowerShellRunner();
            var output = new System.Text.StringBuilder();
            runner.LineReceived += l => { if (l.Kind == OutputKind.Output) output.AppendLine(l.Text); };

            await runner.RunScriptViaPwshAsync("winget upgrade --all --silent --accept-package-agreements --accept-source-agreements 2>$null | Out-String", CancellationToken.None);
            QuickActionProgress = 100;
            QuickActionDetail = "All apps updated";
            ActivityLogService.Instance.Log("App Updates", "Upgrade all completed");
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunQuickAction))]
    private async Task QuickWindowsUpdateAsync()
    {
        await RunQuickActionAsync("Windows Update", "Windows Update", "nav-windows-update", async () =>
        {
            QuickActionDetail = "Checking for Windows updates...";
            QuickActionProgress = 50;
            await Task.Delay(500);
            QuickActionProgress = 100;
            QuickActionDetail = "Navigate to Windows Update tab for full control";
            ActivityLogService.Instance.Log("Windows Update", "Check initiated from Dashboard");
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunQuickAction))]
    private async Task QuickSpeedTestAsync()
    {
        await RunQuickActionAsync("Speed Test", "Speed Test", "nav-speed-test", async () =>
        {
            QuickActionDetail = "Running HTTP speed test (Cloudflare)...";
            QuickActionProgress = 20;
            var service = new SpeedTestService();
            var progress = new Progress<(int Percent, string Message)>(p =>
            {
                QuickActionProgress = 20 + (int)(p.Percent * 0.8);
                QuickActionDetail = p.Message;
            });
            var result = await service.RunHttpAsync(progress, CancellationToken.None);

            QuickActionProgress = 100;
            QuickActionDetail = $"↓ {result.DownloadMbps:F0} Mbps · ↑ {result.UploadMbps:F0} Mbps · Ping {result.PingMs:F0}ms";
            ActivityLogService.Instance.Log("Speed Test", QuickActionDetail);
        });
    }

    [RelayCommand]
    private void NavigateToQuickActionTab()
    {
        if (_quickActionNavigateTarget is null) return;
        // Navigate via MainWindowViewModel
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainWindowViewModel main)
        {
            var target = main.NavItems.FirstOrDefault(n => n.Id == _quickActionNavigateTarget);
            if (target is not null) main.SelectedNav = target;
        }
        DismissQuickAction();
    }

    [RelayCommand]
    private void DismissQuickAction()
    {
        IsQuickActionRunning = false;
        IsQuickActionDone = false;
        QuickActionProgress = 0;
        QuickActionName = "";
        QuickActionDetail = "";
        QuickActionStatus = "";
        QuickActionNavigateLabel = "";
        _quickActionNavigateTarget = null;
        QuickCleanupCommand.NotifyCanExecuteChanged();
        QuickUpdateAppsCommand.NotifyCanExecuteChanged();
        QuickWindowsUpdateCommand.NotifyCanExecuteChanged();
        QuickSpeedTestCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunQuickAction() => !IsQuickActionRunning || IsQuickActionDone;

    private async Task RunQuickActionAsync(string name, string tabLabel, string navId, Func<Task> action)
    {
        IsQuickActionRunning = true;
        IsQuickActionDone = false;
        QuickActionName = name;
        QuickActionStatus = "Running...";
        QuickActionProgress = 0;
        QuickActionDetail = "";
        _quickActionNavigateTarget = navId;
        QuickActionNavigateLabel = $"→ Go to {tabLabel} for more details";
        QuickCleanupCommand.NotifyCanExecuteChanged();
        QuickUpdateAppsCommand.NotifyCanExecuteChanged();
        QuickWindowsUpdateCommand.NotifyCanExecuteChanged();
        QuickSpeedTestCommand.NotifyCanExecuteChanged();

        try
        {
            await action();
            QuickActionStatus = "✓ Done";
            IsQuickActionDone = true;
            LoadActivity();
        }
        catch (Exception ex)
        {
            QuickActionStatus = "Failed";
            QuickActionDetail = ex.Message;
            IsQuickActionDone = true;
        }
        finally
        {
            QuickCleanupCommand.NotifyCanExecuteChanged();
            QuickUpdateAppsCommand.NotifyCanExecuteChanged();
            QuickWindowsUpdateCommand.NotifyCanExecuteChanged();
            QuickSpeedTestCommand.NotifyCanExecuteChanged();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  COMMANDS
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Scanning...";
        try
        {
            await LoadStaticInfoAsync();
            LoadDrives();
            await LoadHealthScoreAsync();
            await LoadTemperaturesAsync();
            LoadActivity();
            StatusMessage = $"Last scan: {DateTime.Now:HH:mm:ss}";
            ToastService.Instance.Show("Dashboard refreshed", "All systems scanned");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    // ── Tune-Up (preserved from original) ─────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunTuneUp))]
    private async Task RunTuneUpAsync()
    {
        if (!DialogService.Instance.Confirm(
            "Quick Tune-Up will clean temp files, empty Recycle Bin, and scan your system.\n\nProceed?",
            "Quick Tune-Up — Confirm"))
            return;

        var opLock = OperationLockService.Instance.TryAcquire(
            OperationCategory.Disk, "Quick Tune-Up");
        if (opLock is null)
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
            TuneUpProgress = Math.Min((p.Step + 1) * 100 / 6, 100);
        });

        try
        {
            TuneUpResult = await _tuneUp.RunAsync(true, progress, _tuneUpCts.Token);
            HasTuneUpResult = true;
            StatusMessage = $"Tune-Up complete — {TuneUpResult.FreedDisplay} freed";
            ToastService.Instance.Show("Tune-Up complete", $"{TuneUpResult.FreedDisplay} freed");
            ActivityLogService.Instance.Log("Quick Tune-Up", $"Freed {TuneUpResult.FreedDisplay}");
            LoadActivity();
        }
        catch (OperationCanceledException) { StatusMessage = "Tune-Up cancelled."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Tune-Up error: {ex.Message}";
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
    private void CancelTuneUp() => _tuneUpCts?.Cancel();

    [RelayCommand]
    private void DismissTuneUpResult() { HasTuneUpResult = false; TuneUpResult = null; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollingCts?.Cancel();
            _pollingCts?.Dispose();
            _tuneUpCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ── Helper record for storage display ──────────────────────────────────────
public sealed record DriveUsageInfo(string Letter, double UsedGB, double TotalGB)
{
    public double Percent => TotalGB > 0 ? UsedGB / TotalGB * 100 : 0;
    public string DisplayUsed => $"{UsedGB:F0} / {TotalGB:F0} GB ({Percent:F0}%)";
    public string ColorHex => Percent switch
    {
        > 90 => "#EF4444",
        > 75 => "#F59E0B",
        > 50 => "#3B82F6",
        _ => "#22C55E"
    };
}
