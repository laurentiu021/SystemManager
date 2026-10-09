// SysManager · DashboardViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
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
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    // Quick Tune-Up and the slowdown check both take the Disk lock, so at most one of them runs.
    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsTuneUpRunning ? CancelTuneUpCommand : IsSlowdownCheckRunning ? CancelSlowdownCheckCommand : null;

    private readonly SystemInfoService _sys;
    private readonly ITuneUpService _tuneUp;
    private readonly HealthScoreService _healthScore;
    private readonly TemperatureService _temps;
    private readonly IWingetService _winget;
    private readonly CrashMarkerService _crashMarkers;

    // The 30-day WHEA scan the memory alert is named after. Already a registered singleton; it was simply
    // never reached from here, so the alert classified on a usage percentage instead.
    private readonly MemoryTestService _memTest;
    private readonly INavigationService _navigation;

    // The same Windows Update Agent seam the Windows Update tab scans through, for the quick action that
    // checks from here. It scans only; installing stays on the tab, where the updates can be chosen.
    private readonly IWindowsUpdateService _windowsUpdate;

    // The quick speed test runs the Speed Test tab's engine and records into the tab's own history, so the
    // result is where speed results live rather than in Recent Activity, which lists changes to the PC.
    private readonly ISpeedTestService _speedTest;
    private readonly SpeedTestHistoryService _speedHistory;

    // Null when no caller supplied one, which omits the stranded-block alert entirely. See the
    // constructor's appBlocker parameter for why it is optional.
    private readonly IAppBlockerService? _appBlocker;

    // Null when no caller supplied one, which leaves "Why is it slow?" unable to run. See the constructor's slowdown
    // parameter for why it is optional.
    private readonly ISlowdownService? _slowdown;

    // Null when no caller supplied one, which leaves the question about a game mode session left on to Gaming Profile.
    // See the constructor's gaming parameter.
    private readonly IGamingProfileService? _gaming;

    private CancellationTokenSource? _tuneUpCts;
    private CancellationTokenSource? _slowdownCts;
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

    /// <summary>
    /// True once the health scan has run and found nothing worth recommending — good news, and something
    /// the card should say rather than leaving its heading over empty space.
    /// </summary>
    /// <remarks>
    /// A flag rather than <c>HealthResult.Recommendations.Count</c>, because before the scan finishes
    /// <c>HealthResult</c> is null: the count binding fails, and a failed binding leaves Visibility at its
    /// default, so the card would announce "nothing needs attention" before anything had been checked.
    /// </remarks>
    [ObservableProperty] private bool _healthHasNothingToImprove;

    /// <summary>
    /// True once a temperature read has completed and returned no sensors at all.
    /// </summary>
    /// <remarks>
    /// Common rather than exotic: most machines expose nothing readable without administrator, and some
    /// expose nothing either way. The card previously rendered as an empty box, which reads as a broken
    /// feature. Set after the read so it cannot flash while the first one is still in flight.
    /// </remarks>
    [ObservableProperty] private bool _temperaturesUnavailable;

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

    // ── Why is it slow? (#1529) ──────────────────────────────────────────
    [ObservableProperty] private bool _isSlowdownCheckRunning;
    [ObservableProperty] private SlowdownReport? _slowdownReport;
    [ObservableProperty] private bool _hasSlowdownReport;

    /// <summary>The five things the check looks at, each marked as it is done with, while the check runs.</summary>
    public BulkObservableCollection<SlowdownProbeStatus> SlowdownProbes { get; } = new();

    // ── IsActive (pause polling when tab not visible) ────────────────────
    [ObservableProperty] private bool _isActive;

    /// <summary>
    /// Wires the dashboard's services and starts <c>InitAsync</c> fire-and-forget: it reports any previous
    /// crash, loads static info, drives, activity and the health score, then starts the vitals, temperature
    /// and alert loops. Elevation is read once here because it cannot change without relaunching the app.
    /// </summary>
    /// <param name="tuneUp">
    /// The Quick Tune-Up and Quick Cleanup both run through it. Tests pass a substitute, because the real one
    /// deletes the temp files of whatever machine runs them and empties its Recycle Bin.
    /// </param>
    /// <param name="crashMarkers">
    /// Required, not optional. It used to default to <c>new CrashMarkerService()</c>, which resolved
    /// the real profile — and because <see cref="CrashMarkerService.TakePending"/> CONSUMES the marker
    /// by deleting it, every test that constructed this ViewModel silently ate a genuine crash report
    /// before the user could ever be told about it. The convenience of an optional argument is not
    /// worth a defaulting path into user data; the DI container and the designer graph both have one
    /// to hand (#1772).
    /// </param>
    /// <param name="windowsUpdate">
    /// Required, for the quick action that checks Windows Update. A default would be the real Windows
    /// Update Agent, and any test that ran the action would then search Microsoft's servers for real.
    /// </param>
    /// <param name="speedTest">Required for the same reason: the real engine downloads from Cloudflare.</param>
    /// <param name="speedHistory">
    /// The Speed Test tab's history, shared, so a quick test from here shows up there. Tests pass one over a
    /// temporary folder, never the user's own file.
    /// </param>
    /// <param name="appBlocker">
    /// Reads the IFEO blocked list for the stranded-machine alert. Optional so the six existing
    /// construction sites keep compiling unchanged — the same shape <see cref="AboutViewModel"/> uses for
    /// its diagnostics service. Null omits the sixth alert, which is also what makes it testable in both
    /// directions: a test supplies a service over a redirected registry hive to assert it fires, and omits
    /// it to assert nothing else moved.
    /// </param>
    /// <param name="slowdown">
    /// The "Why is it slow?" check (#1529). Optional for the same reason as <paramref name="appBlocker"/>: the six
    /// construction sites written before it keep compiling unchanged. Null leaves the button unable to run, and the
    /// container and the designer graph both supply the real one. A test passes a substitute, because the real one reads
    /// whatever machine runs it.
    /// </param>
    /// <param name="gaming">
    /// Asks, once the Dashboard has loaded, about a game mode session a previous run left on (#2592). Optional for the
    /// same reason as <paramref name="appBlocker"/>; null asks nothing, and Gaming Profile then asks when it is opened.
    /// </param>
    public DashboardViewModel(SystemInfoService sys, ITuneUpService tuneUp,
        HealthScoreService healthScore, TemperatureService temps, IWingetService winget,
        CrashMarkerService crashMarkers, MemoryTestService memTest, INavigationService navigation,
        IWindowsUpdateService windowsUpdate, ISpeedTestService speedTest, SpeedTestHistoryService speedHistory,
        IAppBlockerService? appBlocker = null, ISlowdownService? slowdown = null, IGamingProfileService? gaming = null)
    {
        _navigation = navigation;
        _windowsUpdate = windowsUpdate;
        _speedTest = speedTest;
        _speedHistory = speedHistory;
        _appBlocker = appBlocker;
        _slowdown = slowdown;
        _gaming = gaming;
        _sys = sys;
        _tuneUp = tuneUp;
        _healthScore = healthScore;
        _temps = temps;
        _winget = winget;
        _crashMarkers = crashMarkers;
        _memTest = memTest;
        IsElevated = AdminHelper.IsElevated();
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        ReportPreviousCrash();
        await LoadStaticInfoAsync();
        LoadDrives();
        LoadActivity();
        StartPollingLoop();   // starts BOTH the vitals and temperature loops
        await LoadHealthScoreAsync();
        _ = StartAlertScans();
        await LoadTemperaturesAsync();

        // Last, so the question comes once the window is up and the Dashboard has filled in, rather than before either.
        if (_gaming is not null && await GamingProfileViewModel.OfferToRevertLeftoverAsync(_gaming) is { } said)
            ToastService.Instance.Show("Gaming Profile", said, autoHideMs: 9000);
    }

    /// <summary>
    /// If the previous run died from an unhandled exception, say so once.
    /// <para>A domain-level unhandled exception kills the process with no UI at all, so the user saw
    /// the window vanish and had nothing to report but "it just closed" — while the app had already
    /// written the details to its log. A toast is used rather than a dialog: this is information, and
    /// a modal blocking the app on every start after one crash would be worse than the crash.</para>
    /// <para>The marker is consumed (deleted) by the read, so one crash notifies exactly once.</para>
    /// </summary>
    private void ReportPreviousCrash()
    {
        var marker = _crashMarkers.TakePending(DateTimeOffset.UtcNow);
        if (marker is null) return;

        ToastService.Instance.Show("SysManager closed unexpectedly last time",
            CrashMarkerService.DescribeForUser(marker), autoHideMs: 9000);
        ActivityLogService.Instance.Log("Shell", "Recorded that the previous session ended unexpectedly");
        Log.Information("Previous session crashed: {Type} in v{Version} at {WhenUtc}",
            marker.ExceptionType, marker.Version, marker.WhenUtc);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  REAL-TIME POLLING (300ms)
    // ══════════════════════════════════════════════════════════════════════

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            // Re-read the activity log whenever this tab is shown. The destructive tabs write their
            // entries from their own ViewModels, and this one only reloaded on init or an explicit
            // refresh — so coming back here after a Deep Cleanup or an uninstall showed a stale card,
            // making the new entries look like they had not registered at all.
            LoadActivity();
        }

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
                    GpuVram = string.Create(CultureInfo.InvariantCulture, $"{memUsed:F1} / {memTotal:F1} GB VRAM");
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
        // Set OUTSIDE the dispatcher hop, deliberately. A plain bool needs no UI thread — WPF marshals
        // property-change notifications itself, and it is collection mutations that must be on it — while
        // inside the hop it would be skipped entirely whenever Application.Current is null, which is every
        // unit test. The state would then be assertable nowhere, which is how the elevation branches in
        // #2102 ended up unpinned.
        TemperaturesUnavailable = readings.Count == 0;

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
    //  SYSTEM ALERTS (real scans at boot and on Scan system, parallel)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The most recent round of alert checks. It completes when every alert in the round has its result.
    /// </summary>
    /// <remarks>
    /// Nothing waits for the round started at launch, because the alerts fill in as their checks finish. Scan
    /// system waits for its round before it says everything was scanned, and a test awaits this to observe a
    /// round deterministically, as <see cref="ViewModelBase.InitializationComplete"/> exposes the load.
    /// </remarks>
    internal Task AlertScans { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Replaces the alerts with a fresh set and starts all of their checks at once.
    /// </summary>
    /// <remarks>
    /// Runs at launch, on Scan system, and after Update All Apps. It used to run at launch only, so the card kept
    /// saying whatever was true when SysManager started: "3 app updates available" stayed under the quick action's
    /// "All apps updated", and Scan system said "All systems scanned" without checking any alert again (#2479).
    /// <para>A new set rather than the old alerts reset. A check still running from an earlier round keeps writing
    /// to the alert it was handed, and that alert has left the list, so a slow old result cannot land over a newer
    /// one.</para>
    /// </remarks>
    private Task StartAlertScans()
    {
        Alerts.Clear();

        var smartAlert = new DashboardAlert { Title = "Checking disk health...", State = AlertLoadingState.Loading };
        var appUpdateAlert = new DashboardAlert { Title = "Checking app updates...", State = AlertLoadingState.Loading };
        var memoryAlert = new DashboardAlert { Title = "Checking memory health...", State = AlertLoadingState.Loading };
        var eventLogAlert = new DashboardAlert { Title = "Checking Event Log...", State = AlertLoadingState.Loading };
        var rebootAlert = new DashboardAlert { Title = "Checking for a pending reboot...", State = AlertLoadingState.Loading };

        Alerts.Add(smartAlert);
        Alerts.Add(appUpdateAlert);
        Alerts.Add(memoryAlert);
        Alerts.Add(eventLogAlert);
        Alerts.Add(rebootAlert);

        var scans = new List<Task>
        {
            RunAlertScanAsync(smartAlert, ScanSmartHealthAsync),
            RunAlertScanAsync(appUpdateAlert, ScanAppUpdatesAsync),
            RunAlertScanAsync(memoryAlert, ScanMemoryHealthAsync),
            RunAlertScanAsync(eventLogAlert, ScanEventLogAsync),
            RunAlertScanAsync(rebootAlert, ScanPendingRebootAsync),
        };

        // Sixth alert, added last and only when the shell supplied the service, because it answers a
        // question the other five do not: has this machine been left in a state it cannot get out of?
        // An IFEO block a build published before #2030 could write on consent.exe removes elevation, and
        // the damage is INVISIBLE until the first time something asks for administrator rights — so the
        // App Blocker tab, where the evidence lives, is the last place anyone would think to look (#2357).
        if (_appBlocker is not null)
        {
            var blockAlert = new DashboardAlert { Title = "Checking blocked applications...", State = AlertLoadingState.Loading };
            Alerts.Add(blockAlert);
            scans.Add(RunAlertScanAsync(blockAlert, ScanUnrecoverableBlocksAsync));
        }

        return AlertScans = Task.WhenAll(scans);
    }

    /// <summary>
    /// Reports an existing IFEO block that could not be lifted from inside the app.
    /// </summary>
    /// <remarks>
    /// Read-only. It reports and routes; it never removes a value, elevated or not — an unattended
    /// registry write is exactly the kind of repair that should be the user's decision, and the Unblock
    /// button already asks for confirmation.
    /// <para>Red rather than yellow when it fires. Every other alert here describes something degraded;
    /// this one describes a machine that cannot grant administrator rights, which is not a shade of
    /// warning.</para>
    /// </remarks>
    private Task ScanUnrecoverableBlocksAsync(DashboardAlert alert)
    {
        var stranded = _appBlocker!.GetBlockedApps()?.Where(a => a.IsUnrecoverable).ToList();
        var (title, severity) = ClassifyStrandedBlocks(stranded?.Count, stranded?.FirstOrDefault()?.ExecutableName);

        UiThread.Post(() =>
        {
            alert.Title = title;
            alert.Severity = severity;
            alert.NavTargetId = NavTargetFor(severity, "nav-app-blocker");
        });

        return Task.CompletedTask;
    }

    /// <summary>Pure decision for the stranded-block alert. Testable without WPF.</summary>
    /// <remarks>
    /// Separated from the scan like the other <c>Classify…</c> methods here, so every wording can be asserted
    /// directly. Most of these checks read the Event Log or the registry, where a test cannot produce each
    /// answer on demand.
    /// <para>Red, not yellow. Every other alert on this page reports something degraded; this one reports a
    /// machine that can no longer grant administrator rights.</para>
    /// <para>A <paramref name="count"/> of null is a block list that could not be read. It used to arrive as an
    /// empty list and read "No blocked apps need attention", in green (#2503).</para>
    /// </remarks>
    internal static (string Title, AlertSeverity Severity) ClassifyStrandedBlocks(int? count, string? firstName) =>
        count switch
        {
            null => ("Blocked apps could not be checked", AlertSeverity.Yellow),
            0 => ("No blocked apps need attention", AlertSeverity.Green),
            1 => ($"{firstName} is blocked and cannot be unblocked normally", AlertSeverity.Red),
            _ => ($"{count} blocked apps cannot be unblocked normally", AlertSeverity.Red),
        };

    /// <summary>
    /// Says "this is taking a moment" after five seconds, and deliberately does NOT say how much
    /// longer.
    /// <para>It used to read "~10s remaining", which was a string literal — nothing measured it, and it
    /// said the same thing whether the check finished in 300&#160;ms or stalled for a minute. These five
    /// checks are pass/fail probes with no progress signal, so there is nothing for
    /// <see cref="Helpers.EtaCalculator"/> to smooth; the tabs that DO report progress (App Updates,
    /// Bulk Installer) feed it real samples. Inventing a number where none exists is the same mistake as
    /// promising a restore point that was never created.</para>
    /// <para>The mutation must run on the UI thread — <paramref name="alert"/> is a bound
    /// ObservableObject, so raising PropertyChanged off the thread-pool thread can throw or fail to
    /// update. <c>BeginInvoke</c> here, not the <c>UiThread.Post</c> the scanners write their results
    /// through: with no dispatcher, Post runs the action inline, and this re-check of <c>State</c> would
    /// then race <see cref="RunAlertScanAsync"/>'s <c>finally</c> on another thread. Without a dispatcher
    /// there is nobody to show the hint to, so dropping it is the right answer.</para>
    /// </summary>
    private static async Task AcknowledgeSlowScanAsync(DashboardAlert alert, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   // the scan finished first, so there is nothing to acknowledge
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Re-checked on the UI thread: the scan can complete between the delay ending and this
            // running, and a hint that appears after the result has landed would be nonsense.
            if (alert.State == AlertLoadingState.Loading)
            {
                alert.ShowEta = true;
                alert.Eta = "still checking…";
            }
        });
    }

    private static async Task RunAlertScanAsync(DashboardAlert alert, Func<DashboardAlert, Task> scanner)
    {
        // After 5s the wait is worth acknowledging — but WITHOUT naming a duration. Cancelled as
        // soon as the scan ends, so a fast check leaves nothing waiting to wake up.
        using var hintCts = new CancellationTokenSource();
        _ = AcknowledgeSlowScanAsync(alert, hintCts.Token);

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
            // Before the flags below: once the scan is done the hint has nothing left to say, and
            // this is what stops five delays per dashboard load outliving the work they described.
            hintCts.Cancel();
            alert.State = AlertLoadingState.Complete;
            alert.ShowEta = false;
        }
    }

    private async Task ScanSmartHealthAsync(DashboardAlert alert)
    {
        // Reuse the score LoadHealthScoreAsync already computed (it runs before the alert
        // scans) instead of recomputing the heavy WMI/SMART/battery work; fall back to a fresh
        // compute only if that load produced nothing (e.g. it failed).
        var result = HealthResult ?? await _healthScore.ComputeAsync();
        var (title, severity) = ClassifySmartHealth(
            result.DiskScore, result.IsUnavailable(HealthScoreService.DiskComponent));

        UiThread.Post(() =>
        {
            alert.Title = title;
            alert.Severity = severity;
            alert.NavTargetId = NavTargetFor(severity, "nav-system-health");
        });
    }

    /// <summary>Pure decision for the SMART alert. Testable without WPF.</summary>
    /// <remarks>
    /// The unavailable case has to come first and cannot be inferred from the score. DiskHealthService
    /// swallows WMI failures and returns an empty list, which used to score 100 and land in the green branch —
    /// so a machine whose disk health could not be read was told "All SMART indicators healthy". Scoring it as
    /// unknown instead would put it in the "degrading" branch, which is a different wrong answer: nothing is
    /// degrading, nothing was measured.
    /// </remarks>
    internal static (string Title, AlertSeverity Severity) ClassifySmartHealth(int diskScore, bool unavailable)
    {
        if (unavailable)
            return ("Disk health could not be read", AlertSeverity.Yellow);

        return diskScore switch
        {
            >= 90 => ("All SMART indicators healthy", AlertSeverity.Green),
            >= 60 => ("Disk health degrading", AlertSeverity.Yellow),
            _ => ("Disk health critical — immediate attention needed", AlertSeverity.Red)
        };
    }

    private async Task ScanAppUpdatesAsync(DashboardAlert alert)
    {
        int? count = null;
        try
        {
            // Reuse the shared, column-parsed upgrade list instead of a fragile
            // "count non-blank lines minus the header/separator" heuristic — the
            // latter mis-counted whenever winget's header/footer layout shifted.
            var upgradable = await _winget.ListUpgradableAsync(CancellationToken.None);
            count = upgradable.Count;
        }
        // The two failures App Updates names: the query failed, or winget is not installed.
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Debug("Alert scan failed: {Error}", ex.Message);
        }

        var (title, severity) = ClassifyAppUpdates(count);
        UiThread.Post(() =>
        {
            alert.Title = title;
            alert.Severity = severity;
            alert.NavTargetId = NavTargetFor(severity, "nav-app-updates");
        });
    }

    /// <summary>Pure decision for the App Updates alert. Testable without WPF.</summary>
    /// <remarks>
    /// Null means the check itself failed. That is yellow, like every other result nobody could read, and it
    /// used to be green: the failure branch wrote "App update check unavailable" in the colour of "all good"
    /// (#2479).
    /// </remarks>
    internal static (string Title, AlertSeverity Severity) ClassifyAppUpdates(int? count) =>
        count switch
        {
            null => ("App updates could not be checked", AlertSeverity.Yellow),
            0 => ("All apps up to date", AlertSeverity.Green),
            _ => ($"{count} app update{(count == 1 ? "" : "s")} available", AlertSeverity.Yellow),
        };

    private async Task ScanMemoryHealthAsync(DashboardAlert alert)
    {
        // Reads the log, not the usage meter. This alert is titled after a 30-day hardware-error verdict, and
        // it used to classify on RamScore — which HealthScoreService derives entirely from memory USAGE
        // percent, a number with no relation to hardware errors. Both directions were wrong: real WHEA errors
        // at 40% usage read as "No memory errors", and a healthy machine at 85% usage read as "Memory issues
        // detected" while the System Health tab said the opposite. CheckErrorLogsAsync is the method whose
        // whole purpose is this question, and it is already a registered singleton.
        MemoryTestService.MemoryErrorSummary? summary = null;
        try
        {
            summary = await _memTest.CheckErrorLogsAsync().ConfigureAwait(false);
        }
        catch (System.Diagnostics.Eventing.Reader.EventLogException ex)
        {
            Log.Warning("Dashboard: memory error scan failed: {Error}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning("Dashboard: memory error scan denied: {Error}", ex.Message);
        }

        var (title, severity) = ClassifyMemoryHealth(summary);
        UiThread.Post(() =>
        {
            alert.Title = title;
            alert.Severity = severity;
            // System Health, where the memory check and the memory test live. #1496 took "— see System Health"
            // out of this alert's wording so a Fix this link could say it instead, and no link was ever set
            // here, so "test your RAM" offered no way to get there.
            alert.NavTargetId = NavTargetFor(severity, "nav-system-health");
        });
    }

    /// <summary>Pure decision for the memory alert. Testable without WPF.</summary>
    /// <remarks>
    /// Worded to match <c>SystemHealthViewModel</c>'s verdicts for the same summary, so the Dashboard tile and
    /// the System Health tab can never disagree about the same 30 days. A null summary means the scan itself
    /// failed, which is reported as unknown rather than as good news.
    /// </remarks>
    internal static (string Title, AlertSeverity Severity) ClassifyMemoryHealth(
        MemoryTestService.MemoryErrorSummary? summary)
    {
        if (summary is null)
            return ("Memory errors could not be checked", AlertSeverity.Yellow);

        if (summary.WheaMemoryErrors > 0)
            return ($"{summary.WheaMemoryErrors} memory hardware error{(summary.WheaMemoryErrors == 1 ? "" : "s")} in 30 days — test your RAM",
                AlertSeverity.Red);

        if (summary.MemoryDiagnosticResults > 0)
            return ($"Memory diagnostic ran {summary.MemoryDiagnosticResults} time{(summary.MemoryDiagnosticResults == 1 ? "" : "s")} — check results",
                AlertSeverity.Yellow);

        return ("No memory errors (30 days)", AlertSeverity.Green);
    }

    private Task ScanEventLogAsync(DashboardAlert alert)
    {
        int? criticalCount = null;
        try
        {
            var since = DateTime.Now.AddDays(-7);
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                "System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[Level=1 and TimeCreated[@SystemTime>='{since.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}']]]");

            var counted = 0;
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            // Each EventRecord wraps an unmanaged EVT_HANDLE and must be disposed —
            // discarding them (as before) leaked a native handle per critical event.
            System.Diagnostics.Eventing.Reader.EventRecord? rec;
            while ((rec = reader.ReadEvent()) is not null)
            {
                using (rec) counted++;
            }
            criticalCount = counted;
        }
        // The two failures the memory check names for the same log: it could not be read, or reading was refused.
        catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException)
        {
            Log.Debug("Alert scan failed: {Error}", ex.Message);
        }

        var (title, severity) = ClassifyEventLog(criticalCount);
        UiThread.Post(() =>
        {
            alert.Title = title;
            alert.Severity = severity;
            // Logs, not System Health. This line was written twice with different tabs and the
            // second won, so the button read as "take me to the events" and opened a page that does
            // not list them. The title names the Event Log, so that is where it has to go (#2359).
            alert.NavTargetId = NavTargetFor(severity, "nav-logs");
        });
        return Task.CompletedTask;
    }

    /// <summary>Pure decision for the Event Log alert. Testable without WPF.</summary>
    /// <remarks>Null means the log could not be read, which is yellow rather than the green it used to be (#2479).</remarks>
    internal static (string Title, AlertSeverity Severity) ClassifyEventLog(int? criticalCount) =>
        criticalCount switch
        {
            null => ("Event Log could not be checked", AlertSeverity.Yellow),
            0 => ("No critical events (last 7 days)", AlertSeverity.Green),
            _ => ($"{criticalCount} critical event{(criticalCount == 1 ? "" : "s")} in Event Log (last 7d)", AlertSeverity.Red),
        };

    private Task ScanPendingRebootAsync(DashboardAlert alert)
    {
        bool? pending = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            pending = key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Debug("Alert scan failed: {Error}", ex.Message);
        }

        var (title, severity) = ClassifyPendingReboot(pending);
        UiThread.Post(() =>
        {
            alert.Title = title;
            alert.Severity = severity;
            alert.NavTargetId = NavTargetFor(severity, "nav-windows-update");
        });
        return Task.CompletedTask;
    }

    /// <summary>Pure decision for the pending-reboot alert. Testable without WPF.</summary>
    /// <remarks>
    /// Null means the key could not be read. That is yellow, not the green "Feature check unavailable" it used to
    /// be, which also named a check this alert does not make (#2479).
    /// <para>A null check and a plain <c>bool</c>, not a switch. CodeQL's constant-condition rule reads a <c>false</c>
    /// pattern that follows <c>true</c> as always matching, even on a <c>bool?</c> where null is still possible, and it
    /// reported the switch in both orders it has been written in. Whatever the shape, the unknown case must not reach
    /// green: only a definite <c>false</c> does.</para>
    /// </remarks>
    internal static (string Title, AlertSeverity Severity) ClassifyPendingReboot(bool? pending) =>
        pending is not { } required
            ? ("Pending reboot could not be checked", AlertSeverity.Yellow)
            : required
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
            HealthHasNothingToImprove = HealthResult.Recommendations.Count == 0;
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
            // The Disk lock the Cleanup tab's temp clean and the Quick Tune-Up take around this same sweep. Without
            // it, two sweeps of one folder race, and each reports only the part it happened to delete (#2473).
            using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Quick Cleanup");
            if (opLock is null)
            {
                throw new InvalidOperationException(
                    $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.");
            }

            QuickActionDetail = "Cleaning temp files...";
            QuickActionProgress = 50;

            // Delegate to the shared TuneUpService cleaner: it cleans BOTH user and Windows
            // TEMP and never follows reparse points (junctions / symlinks) out of the temp
            // tree, so it can't be redirected into unrelated user data. This replaces an
            // earlier inline cleaner that only scanned the user TEMP top level and swallowed
            // every error.
            var (freed, _, _) = await _tuneUp.CleanTempFilesAsync(CancellationToken.None);

            QuickActionProgress = 100;
            var freedMB = freed / 1024.0 / 1024.0;
            QuickActionDetail = freedMB >= 1024
                ? string.Create(CultureInfo.InvariantCulture, $"Freed {freedMB / 1024:F1} GB")
                : $"Freed {freedMB:F0} MB";
            ActivityLogService.Instance.Log("Quick Cleanup", QuickActionDetail);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunQuickAction))]
    private async Task QuickUpdateAppsAsync()
    {
        if (!DialogService.Instance.Confirm(
                "Upgrade all installed apps that have updates available via winget?\n\n" +
                WingetFailure.UpgradeWarning,
                "Confirm Update All Apps"))
            return;

        await RunQuickActionAsync("Update All Apps", "App Updates", "nav-app-updates", async () =>
        {
            // The install lock App Updates, Bulk Installer and Uninstaller take around their own winget runs. Without it,
            // this ran beside one of them, and an MSI package in either could fail with 1618, because Windows Installer
            // runs one installation at a time (#2553).
            using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Install, "Update All Apps");
            if (opLock is null)
            {
                throw new InvalidOperationException(
                    $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Install)} is already running.");
            }

            QuickActionDetail = "Checking for upgrades...";
            QuickActionProgress = 30;
            // Delegate to the injected WingetService (the IPowerShellRunner-backed seam) so the
            // Dashboard's one-click "Update All Apps" uses the SAME winget invocation as the App
            // Updates tab. A hand-rolled command here had drifted — it was missing
            // --no-progress / --disable-interactivity / --include-unknown and reported success
            // even when winget failed.
            var result = await _winget.UpgradeAllAsync(CancellationToken.None);
            QuickActionProgress = 100;
            ActivityLogService.Instance.Log("App Updates",
                result.Succeeded ? "Upgrade all completed" : $"Upgrade all: {result.FriendlyMessage}");
            // Checked again whether or not every package upgraded, because either way the number the app update
            // alert showed is out of date, and it used to stay beside "All apps updated" (#2479).
            _ = StartAlertScans();
            // RunQuickActionAsync reports "✓ Done" for any action that returns, so a failed run must not return:
            // `winget upgrade --all` exits non-zero when any one package fails, and the card used to read "✓ Done"
            // above the failure text (#2437). Its failure branch shows the message as the detail.
            if (!result.Succeeded)
                throw new InvalidOperationException(result.FriendlyMessage);
            QuickActionDetail = "All apps updated";
        });
    }

    /// <summary>
    /// Checks Windows Update from here and says what it found. The card's link then opens the Windows Update
    /// tab, where updates are chosen and installed.
    /// </summary>
    /// <remarks>
    /// This was a check in name only (#2437) — a progress bar, half a second's wait and "✓ Done" — and then only
    /// a link to the tab. It now runs the tab's own scan, through the same seam, and installs nothing: the scan
    /// also lists optional drivers and feature upgrades, and choosing among those is what the tab is for. A
    /// failed search ends as "Failed" with the reason, never as up to date. Nothing is written to Recent
    /// Activity, because a check changes nothing, and the Windows Update tab's own scan does not log either.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunQuickAction))]
    private async Task QuickWindowsUpdateAsync()
    {
        await RunQuickActionAsync("Check Windows Updates", "Windows Update", "nav-windows-update", async () =>
        {
            QuickActionDetail = "Asking Windows Update what is available…";
            QuickActionProgress = 30;

            IReadOnlyList<UpdateEntry> found;
            try
            {
                found = await _windowsUpdate.ScanAsync(CancellationToken.None);
            }
            // The card shows the message as its detail, and a COM message is an HRESULT in jargon. These are the
            // Windows Update tab's own words for the same two failures.
            catch (System.Runtime.InteropServices.COMException ex)
            {
                throw new InvalidOperationException($"Windows Update Agent error: 0x{ex.HResult:X8}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException("Access denied — run SysManager as administrator.", ex);
            }

            QuickActionProgress = 100;
            QuickActionDetail = DescribeWindowsUpdateCheck(found.Count);
        });
    }

    /// <summary>What the Windows Update check found, in the card's words. Pure, so it is testable without WPF.</summary>
    internal static string DescribeWindowsUpdateCheck(int available) =>
        available == 0
            ? "Windows is up to date"
            : $"{available} update{(available == 1 ? "" : "s")} available";

    /// <summary>
    /// Runs the Speed Test tab's HTTP test from here and records the result in that tab's history.
    /// </summary>
    /// <remarks>
    /// The result used to go into Recent Activity, which lists what SysManager changed on the PC, while the Speed
    /// Test tab, where results are kept and compared, never saw it. It now goes into the tab's own history, and
    /// Recent Activity is left alone, as it is for a test run on the tab. It also takes the network lock the tab
    /// takes: two tests at once would each measure about half the line, and both readings would now land in the
    /// history the tab compares against.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunQuickAction))]
    private async Task QuickSpeedTestAsync()
    {
        await RunQuickActionAsync("Speed Test", "Speed Test", "nav-speed-test", async () =>
        {
            using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "HTTP Speed Test");
            if (opLock is null)
            {
                throw new InvalidOperationException(
                    $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Network)} is already running.");
            }

            QuickActionDetail = "Running HTTP speed test (Cloudflare)...";
            QuickActionProgress = 20;
            var progress = new SettlingProgress<(int Percent, string Message)>(p =>
            {
                QuickActionProgress = 20 + (int)(p.Percent * 0.8);
                QuickActionDetail = p.Message;
            });
            var result = await progress.SettleAfterAsync(
                reporter => _speedTest.RunHttpAsync(reporter, CancellationToken.None));

            QuickActionProgress = 100;
            QuickActionDetail = DescribeSpeedTest(result, saved: await _speedHistory.SaveAsync(result));
        });
    }

    /// <summary>
    /// The quick test's result line. A result that could not be written says so, as the Speed Test tab does,
    /// because a reading the user expects to find in the history and cannot is worse than a warning.
    /// </summary>
    /// <remarks>
    /// An upload or ping that was not measured reads "—", as on the Speed Test tab. A ping with no answer used to
    /// read "Ping 0ms", a perfect score (#2504).
    /// </remarks>
    internal static string DescribeSpeedTest(SpeedTestResult result, bool saved)
    {
        var up = result.UploadMbps is { } u ? $"{u:F0} Mbps" : "—";
        var ping = result.PingMs is { } p ? $"{p:F0}ms" : "—";
        return $"↓ {result.DownloadMbps:F0} Mbps · ↑ {up} · Ping {ping}"
            + (saved ? "" : " — not saved to the Speed Test history");
    }

    [RelayCommand]
    private void NavigateToQuickActionTab()
    {
        if (_quickActionNavigateTarget is null) return;
        SelectTab(_quickActionNavigateTarget);
        DismissQuickAction();
    }

    /// <summary>
    /// Opens the tab with the given nav id. Used by the Tune-Up result card so each finding links to the
    /// tab that can act on it — "3 broken shortcuts" is only useful if it can take you to the cleaner.
    /// <para>Shares one implementation with <see cref="NavigateToQuickActionTabCommand"/> rather than
    /// duplicating the shell lookup, and deliberately does NOT dismiss the card: the user may want to
    /// come back to the remaining findings.</para>
    /// </summary>
    [RelayCommand]
    private void OpenTab(string? navId)
    {
        if (!string.IsNullOrEmpty(navId)) SelectTab(navId);
    }

    /// <summary>
    /// Where an alert of this severity sends the user: the given tab, or nowhere when it is green.
    /// </summary>
    /// <remarks>
    /// A green alert has nothing to fix, so offering "Fix this" beside "All SMART indicators healthy"
    /// would teach the user that the button means nothing. The rule is one function rather than five
    /// copies of a ternary so it can be asserted once, and each scan names its own destination next to
    /// the classification that produced the severity (#1496).
    /// <para>Not folded into the Classify* helpers, which return <c>(Title, Severity)</c> and are what the
    /// tests assert against: widening those tuples to carry a nav id would rewrite every one of those
    /// assertions to express a mapping that has nothing to do with what they classify.</para>
    /// </remarks>
    internal static string NavTargetFor(AlertSeverity severity, string navId)
        => severity == AlertSeverity.Green ? "" : navId;

    /// <summary>Opens a tab through the shell's navigation seam.</summary>
    /// <remarks>
    /// Was <c>Application.Current.MainWindow.DataContext as MainWindowViewModel</c> — a service locator
    /// reaching for a live window, untestable and silently inert whenever no window is up. #1504 pointed
    /// out that copying it to the next caller would deepen the anti-pattern, so it became an injected
    /// <see cref="INavigationService"/> instead, which a test can substitute and assert against.
    /// </remarks>
    private void SelectTab(string navId, string? filter = null) => _navigation.GoTo(navId, filter);

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
            // After the health score, as at launch, because the disk alert reads the score just computed.
            var alerts = StartAlertScans();
            await LoadTemperaturesAsync();
            LoadActivity();
            // Waited for before the toast below says every system was scanned. The alerts are part of that.
            await alerts;
            StatusMessage = $"Last scan: {DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
            ToastService.Instance.Show("Dashboard refreshed", "All systems scanned");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    // ── Tune-Up (preserved from original) ─────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunTuneUp))]
    private async Task RunTuneUpAsync()
    {
        // Says that the bin is emptied for good, as Quick Cleanup does: the Tune-Up always empties it (#2505). The temp
        // files are deleted outright too, so they are named as well (#2611).
        if (!DialogService.Instance.Confirm(
            "Quick Tune-Up will delete temp files, permanently empty the Recycle Bin, and scan your system.\n\n"
            + "Neither the temp files nor what is in the Recycle Bin can be recovered afterwards.\n\nProceed?",
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

    // ── Why is it slow? (#1529) ───────────────────────────────────────────

    /// <summary>The name the Disk lock is held under while the check runs, which another tab's refusal names.</summary>
    internal const string SlowdownCheckName = "Dashboard slowness check";

    /// <summary>
    /// Looks at the five things that most often slow a PC down, all at once, and shows what it found, worst first, each with
    /// the tab that can act on it.
    /// </summary>
    /// <remarks>
    /// Takes the Disk lock, as Quick Tune-Up does. A cleanup or a disk scan running in SysManager would be what the check
    /// finds, and the check reads the process list and the drives while it works. Asks nothing first: it only reads, and
    /// every button on what it finds only opens a tab. Nothing goes into Recent Activity, which lists what changed, and a
    /// check changes nothing.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCheckSlowdown))]
    private async Task CheckSlowdownAsync()
    {
        var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, SlowdownCheckName);
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }

        using var opLockGuard = opLock;
        _slowdownCts = new CancellationTokenSource();
        var token = _slowdownCts.Token;
        IsSlowdownCheckRunning = true;
        HasSlowdownReport = false;
        SlowdownReport = null;
        SlowdownProbes.ReplaceWith(SlowdownProbeStatus.Looking);
        CheckSlowdownCommand.NotifyCanExecuteChanged();

        // Settling, so a probe's last report cannot land after the result: the list is hidden by then either way, and
        // this is what keeps that true rather than lucky.
        var progress = new SettlingProgress<SlowdownProbeStatus>(MarkSlowdownProbe);
        try
        {
            var report = await progress.SettleAfterAsync(reporter => _slowdown!.CheckAsync(reporter, token));
            SlowdownReport = report;
            HasSlowdownReport = true;
            StatusMessage = report.Headline;
        }
        catch (OperationCanceledException) { StatusMessage = "Check cancelled."; }
        finally
        {
            IsSlowdownCheckRunning = false;
            _slowdownCts?.Dispose();
            _slowdownCts = null;
            CheckSlowdownCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheckSlowdown() => _slowdown is not null && !IsSlowdownCheckRunning;

    /// <summary>Marks one of the five as done with, in its place, so the list keeps its order.</summary>
    private void MarkSlowdownProbe(SlowdownProbeStatus status)
    {
        for (var i = 0; i < SlowdownProbes.Count; i++)
        {
            if (SlowdownProbes[i].Probe != status.Probe) continue;
            SlowdownProbes[i] = status;
            return;
        }
    }

    [RelayCommand]
    private void CancelSlowdownCheck() => _slowdownCts?.Cancel();

    [RelayCommand]
    private void DismissSlowdownReport() { HasSlowdownReport = false; SlowdownReport = null; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollingCts?.Cancel();
            _pollingCts?.Dispose();
            _tuneUpCts?.Cancel();
            _tuneUpCts?.Dispose();
            _slowdownCts?.Cancel();
            _slowdownCts?.Dispose();
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
