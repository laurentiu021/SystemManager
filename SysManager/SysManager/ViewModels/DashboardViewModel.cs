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
    private readonly IWingetService _winget;
    private CancellationTokenSource? _tuneUpCts;
    private CancellationTokenSource? _pollingCts;

    // GPU adapter name and usage availability are effectively static for a session,
    // but the polling loop runs every 300ms — so initialise the NVIDIA API at most
    // once and resolve the adapter name only once (via NvAPI, else a single WMI
    // query), instead of re-initialising and re-querying on every tick.
    private bool _nvApiInitTried;
    private bool _nvApiAvailable;
    // Cached NVIDIA GPU handle, resolved once during init instead of re-enumerating every
    // 300ms poll — matches the "resolve static hardware once" pattern used for the GPU name.
    private NvAPIWrapper.GPU.PhysicalGPU? _gpu;
    private bool _gpuNameResolved;

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
        HealthScoreService healthScore, TemperatureService temps, IWingetService winget)
    {
        _sys = sys;
        _tuneUp = tuneUp;
        _healthScore = healthScore;
        _temps = temps;
        _winget = winget;
        IsElevated = AdminHelper.IsElevated();
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        await LoadStaticInfoAsync();
        LoadDrives();
        LoadActivity();
        StartPollingLoop();   // starts BOTH the vitals and temperature loops
        await LoadHealthScoreAsync();
        StartAlertScans();
        await LoadTemperaturesAsync();
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
            var old = _pollingCts;
            _pollingCts = null;
            old?.Cancel();
            old?.Dispose();
        }
    }

    private void StartPollingLoop()
    {
        var old = _pollingCts;
        _pollingCts = new CancellationTokenSource();
        old?.Cancel();
        old?.Dispose();
        var ct = _pollingCts.Token;

        // Temperature polling shares this CTS so it restarts whenever the tab is
        // re-shown (previously it was started once in InitAsync and never resumed
        // after OnIsActiveChanged cancelled the original token).
        StartTemperaturePolling(ct);

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
                    Log.Debug(ex, "Dashboard polling error");
                    await Task.Delay(1000, ct);
                }
            }
        }, ct);
    }

    private void UpdateGpuUsage()
    {
        // Initialise NvAPI at most once per session. Repeated Initialize() calls on
        // the 300ms loop are wasted work; once we know it's unavailable (non-NVIDIA),
        // we never retry and fall through to the one-time WMI name lookup.
        if (!_nvApiInitTried)
        {
            _nvApiInitTried = true;
            try
            {
                NvAPIWrapper.NVIDIA.Initialize();
                // Resolve the GPU handle ONCE and cache it; each poll then reads live usage and
                // memory off this handle instead of re-enumerating all physical GPUs every tick.
                var gpus = NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs();
                _gpu = gpus.Length > 0 ? gpus[0] : null;
                _nvApiAvailable = _gpu is not null;
            }
            catch (Exception ex)
            {
                _nvApiAvailable = false;
                Log.Debug("NVIDIA GPU API unavailable: {Error}", ex.Message);
            }
        }

        if (_nvApiAvailable && _gpu is not null)
        {
            try
            {
                var usage = _gpu.UsageInformation.GPU.Percentage;
                var memTotal = _gpu.MemoryInformation.DedicatedVideoMemoryInkB / 1024.0 / 1024.0;
                var memUsed = (_gpu.MemoryInformation.DedicatedVideoMemoryInkB -
                               _gpu.MemoryInformation.AvailableDedicatedVideoMemoryInkB) / 1024.0 / 1024.0;
                var name = _gpu.FullName;

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    GpuPercent = usage;
                    GpuName = name;
                    GpuVram = $"{memUsed:F1} / {memTotal:F1} GB VRAM";
                });
                return;
            }
            catch (Exception ex)
            {
                Log.Debug("NVIDIA GPU polling error: {Error}", ex.Message);
            }
        }

        // No NVIDIA GPU (NvAPI only covers NVIDIA). The adapter name is static, so
        // resolve it via WMI exactly once — live usage % is NVIDIA-only because it
        // requires vendor-specific APIs.
        if (!_gpuNameResolved)
            UpdateGpuNameFromWmi();
    }

    private void UpdateGpuNameFromWmi()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            using var collection = searcher.Get();
            foreach (System.Management.ManagementObject mo in collection)
            {
                using (mo)
                {
                    var name = mo["Name"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    _gpuNameResolved = true; // static value — don't query WMI again
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        GpuName = name;
                        GpuVram = "";
                    });
                    return; // first adapter is enough
                }
            }
        }
        catch (System.Management.ManagementException ex)
        {
            Log.Debug("WMI GPU name lookup unavailable: {Error}", ex.Message);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Debug("WMI GPU name lookup failed: {Error}", ex.Message);
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
        // The mutation must run on the UI thread — `alert` is a bound ObservableObject,
        // so raising PropertyChanged off the thread-pool thread can throw or fail to
        // update. Marshal onto the dispatcher exactly like the scanner bodies do.
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (alert.State == AlertLoadingState.Loading)
                {
                    alert.ShowEta = true;
                    alert.Eta = "~10s remaining";
                }
            });
        });

        try
        {
            await Task.Run(() => scanner(alert));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Dashboard alert scan failed: {Alert}", alert.Title);
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
        // Reuse the score LoadHealthScoreAsync already computed (it runs before the alert
        // scans) instead of recomputing the heavy WMI/SMART/battery work; fall back to a fresh
        // compute only if that load produced nothing (e.g. it failed).
        var diskScore = (HealthResult ?? await _healthScore.ComputeAsync()).DiskScore;
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
            // Reuse the shared, column-parsed upgrade list instead of a fragile
            // "count non-blank lines minus the header/separator" heuristic — the
            // latter mis-counted whenever winget's header/footer layout shifted.
            var upgradable = await _winget.ListUpgradableAsync(CancellationToken.None);
            var count = upgradable.Count;

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
        // Reuse the already-computed score (see ScanSmartHealthAsync) rather than recomputing.
        var result = HealthResult ?? await _healthScore.ComputeAsync();
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
            // Each EventRecord wraps an unmanaged EVT_HANDLE and must be disposed —
            // discarding them (as before) leaked a native handle per critical event.
            System.Diagnostics.Eventing.Reader.EventRecord? rec;
            while ((rec = reader.ReadEvent()) is not null)
            {
                using (rec) criticalCount++;
            }

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
        if (!DialogService.Instance.Confirm(
                "Delete temporary files from your user and Windows Temp folders?\n\n" +
                "Files in use may be skipped. This cannot be undone.",
                "Confirm Quick Cleanup"))
            return;

        await RunQuickActionAsync("Quick Cleanup", "Cleanup", "nav-cleanup", async () =>
        {
            QuickActionDetail = "Cleaning temp files...";
            QuickActionProgress = 50;

            // Delegate to the shared TuneUpService cleaner: it cleans BOTH user and Windows
            // TEMP and never follows reparse points (junctions / symlinks) out of the temp
            // tree, so it can't be redirected into unrelated user data. This replaces an
            // earlier inline cleaner that only scanned the user TEMP top level and swallowed
            // every error.
            var (freed, _, _) = await TuneUpService.CleanTempFilesAsync(CancellationToken.None);

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
        if (!DialogService.Instance.Confirm(
                "Upgrade all installed apps that have updates available via winget?\n\n" +
                "Apps may restart during the upgrade.",
                "Confirm Update All Apps"))
            return;

        await RunQuickActionAsync("Update All Apps", "App Updates", "nav-app-updates", async () =>
        {
            QuickActionDetail = "Checking for upgrades...";
            QuickActionProgress = 30;
            // Delegate to the injected WingetService (the IPowerShellRunner-backed seam) so the
            // Dashboard's one-click "Update All Apps" uses the SAME winget invocation as the App
            // Updates tab. A hand-rolled command here had drifted — it was missing
            // --no-progress / --disable-interactivity / --include-unknown and reported success
            // even when winget failed.
            var result = await _winget.UpgradeAllAsync(CancellationToken.None);
            QuickActionProgress = 100;
            QuickActionDetail = result.Succeeded ? "All apps updated" : result.FriendlyMessage;
            ActivityLogService.Instance.Log("App Updates",
                result.Succeeded ? "Upgrade all completed" : $"Upgrade all: {result.FriendlyMessage}");
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
            Log.Debug(ex, "Quick action {Name} failed", name);
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
        > 90 => StatusColors.Bad,
        > 75 => StatusColors.Warning,
        > 50 => StatusColors.Info,
        _ => StatusColors.Good
    };
}
