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
        IsTuneUpRunning ? CancelTuneUpCommand
        : IsSlowdownCheckRunning ? CancelSlowdownCheckCommand
        : IsQuickActionCancelable ? CancelQuickActionCommand
        : null;

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

    // About's two switches for what SysManager does online on its own. Read once, at startup, for the app-updates
    // alert (#2613); Scan system and Update All Apps check whatever it says, because they are asked for.
    private readonly UpdateCheckPreferenceService _updateCheck;

    // Null when no caller supplied one, which omits the stranded-block alert entirely. See the
    // constructor's appBlocker parameter for why it is optional.
    private readonly IAppBlockerService? _appBlocker;

    // Null when no caller supplied one, which leaves "Why is it slow?" unable to run. See the constructor's slowdown
    // parameter for why it is optional.
    private readonly ISlowdownService? _slowdown;

    // Null when no caller supplied one, which leaves the question about a game mode session left on to Gaming Profile.
    // See the constructor's gaming parameter.
    private readonly IGamingProfileService? _gaming;
    private readonly EventLogService _eventLog;
    private readonly TimeProvider _clock;

    // Re-reads Recent activity each minute while the tab is shown, so the ages it shows move (#2802).
    private ITimer? _agesTimer;

    // The newest Recent activity entry when the drives and the health score were last read. A different one on coming
    // back to the tab is something done since, which may have freed or used space (#2802).
    private ActivityEntry? _newestAtStorageRead;

    private CancellationTokenSource? _tuneUpCts;
    private CancellationTokenSource? _slowdownCts;
    private CancellationTokenSource? _pollingCts;

    // GPU load and VRAM come from Windows' own counters for every vendor (#2857). The WMI name lookup
    // below is the fallback for when nothing can be measured at all — no WDDM 2.x counters, or a machine
    // between driver states — where the adapter's NAME is still worth showing and is static for a session,
    // so one query answers it rather than one per 300 ms tick.
    private bool _gpuNameResolved;
    private readonly IGpuTelemetry? _gpu;

    // ── Real-time vitals (300ms polling) ──────────────────────────────────
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuName = "";
    [ObservableProperty] private string _cpuCores = "";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _ramUsedGB;
    [ObservableProperty] private double _ramTotalGB;
    [ObservableProperty] private double _ramAvailableGB;
    [ObservableProperty] private string _ramType = "";
    // Nullable, because "nothing can measure this GPU" and "this GPU is idle" are different answers and
    // 0% is a legible number that means the second one. Null drives the "N/A" text and leaves the bar at
    // zero; it is what a machine with no usable GPU counters now shows instead of a confident 0% (#2857).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuPercentDisplay))]
    private double? _gpuPercent;
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _gpuVram = "";

    /// <summary>The GPU figure as the tile shows it: a percentage, or "N/A" when there is no reading.</summary>
    /// <remarks>
    /// One property rather than a bound number with a "%" literal beside it in XAML: with a nullable source
    /// that pair renders a bare "%" on its own when the value is null, which reads like a broken tile.
    /// </remarks>
    public string GpuPercentDisplay => GpuPercent is { } percent
        ? string.Create(CultureInfo.InvariantCulture, $"{percent:F0}%")
        : "N/A";

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

    /// <summary>
    /// True once a temperature read has completed with no CPU temperature because the PawnIO driver is missing.
    /// </summary>
    /// <remarks>
    /// Shows the card's line that says so. Without it, running as administrator promised the CPU temperature and
    /// the relaunch took the CPU row away without a word (#2828).
    /// </remarks>
    [ObservableProperty] private bool _temperaturesNeedDriver;

    // ── Temperatures ─────────────────────────────────────────────────────
    public BulkObservableCollection<TemperatureReading> Temperatures { get; } = new();

    // ── Storage ──────────────────────────────────────────────────────────
    public BulkObservableCollection<DriveUsageInfo> Drives { get; } = new();

    // ── System Alerts ────────────────────────────────────────────────────
    public ObservableCollection<DashboardAlert> Alerts { get; } = new();

    // ── Recent Activity ──────────────────────────────────────────────────
    public BulkObservableCollection<ActivityEntry> RecentActivity { get; } = new();

    // ── Quick Action state ──────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickActionCancelable))]
    private bool _isQuickActionRunning;
    [ObservableProperty] private string _quickActionName = "";
    [ObservableProperty] private string _quickActionStatus = "";
    [ObservableProperty] private string _quickActionDetail = "";
    [ObservableProperty] private int _quickActionProgress;
    // True until the one quick action with a real, continuously-updating percentage (Speed Test) says
    // otherwise. Quick Cleanup, Update All and the Windows Update check only ever have two fixed
    // milestones (e.g. 50 then 100), which is not progress a determinate bar can honestly show (#2859).
    [ObservableProperty] private bool _isQuickActionIndeterminate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickActionCancelable))]
    private bool _isQuickActionDone;
    [ObservableProperty] private string _quickActionNavigateLabel = "";
    private string? _quickActionNavigateTarget;
    private CancellationTokenSource? _quickActionCts;

    /// <summary>Whether Cancel should be offered: a quick action is running and has not yet finished.</summary>
    /// <remarks>
    /// <see cref="IsQuickActionRunning"/> alone is not enough — unlike Tune-Up, which flips
    /// <see cref="IsTuneUpRunning"/> back to false and shows its result in a separate card, a quick
    /// action's card stays bound to <see cref="IsQuickActionRunning"/> for its whole lifetime, success or
    /// failure, until <see cref="DismissQuickActionCommand"/>. Without this, Escape/the Cancel button
    /// would stay live over an already-finished action with nothing left to cancel.
    /// </remarks>
    public bool IsQuickActionCancelable => IsQuickActionRunning && !IsQuickActionDone;

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
    /// <param name="updateCheck">
    /// Whether the app-updates alert may ask winget when SysManager starts (#2613). Required, as
    /// <paramref name="crashMarkers"/> is: a default would read the user's own setting, so a test could pass or fail on
    /// what someone had ticked in About, and leaving it out of a construction would be a way past the switch. Tests pass
    /// one over a temporary folder.
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
    /// <param name="eventLog">
    /// Counts the critical events the Event Log alert reports. Optional for the same reason as
    /// <paramref name="appBlocker"/>; null counts with a service of its own, over the same System log.
    /// </param>
    /// <param name="clock">
    /// Times the minute after which Recent activity is read again while the tab is shown. Null is the system clock; a
    /// test gives one whose timers fire when it says.
    /// </param>
    /// <param name="gpu">
    /// The app's one GPU-telemetry reader, which the container always passes (#2857). Null reads no GPU at all,
    /// which is what a machine with no usable GPU counters gets anyway, and shows "N/A" rather than 0%.
    /// </param>
    public DashboardViewModel(SystemInfoService sys, ITuneUpService tuneUp,
        HealthScoreService healthScore, TemperatureService temps, IWingetService winget,
        CrashMarkerService crashMarkers, MemoryTestService memTest, INavigationService navigation,
        IWindowsUpdateService windowsUpdate, ISpeedTestService speedTest, SpeedTestHistoryService speedHistory,
        UpdateCheckPreferenceService updateCheck,
        IAppBlockerService? appBlocker = null, ISlowdownService? slowdown = null, IGamingProfileService? gaming = null,
        EventLogService? eventLog = null, TimeProvider? clock = null, IGpuTelemetry? gpu = null)
    {
        _eventLog = eventLog ?? new EventLogService();
        _clock = clock ?? TimeProvider.System;
        _gpu = gpu;
        _navigation = navigation;
        _windowsUpdate = windowsUpdate;
        _speedTest = speedTest;
        _speedHistory = speedHistory;
        _updateCheck = updateCheck;
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
        _newestAtStorageRead = NewestActivity();
        await LoadDrivesAsync();
        LoadActivity();
        StartPollingLoop();   // starts BOTH the vitals and temperature loops
        await LoadHealthScoreAsync();
        _ = StartAlertScans(atStartup: true);
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

            // The storage card and the score had the same gap: neither moved after a Deep Cleanup or an uninstall
            // until Scan system (#2802). Read again only when something was logged since, so moving between tabs
            // does not compute the score each time, and not during the start, which reads both itself.
            if (InitializationComplete.IsCompleted && !Equals(NewestActivity(), _newestAtStorageRead))
                _ = RefreshStorageAsync();
        }

        if (value && _pollingCts is null)
            StartPollingLoop();
        else if (!value)
        {
            var old = _pollingCts;
            _pollingCts = null;
            old?.Cancel();
            old?.Dispose();
            _agesTimer?.Dispose();
            _agesTimer = null;
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

        // Recent activity says how long ago each entry was, and a row reads that once, when the list is built, which
        // happened only at start, on coming back to the tab and after the Dashboard's own actions. While the tab
        // stayed shown, "Just now" kept saying it for hours (#2802).
        _agesTimer?.Dispose();
        _agesTimer = _clock.CreateTimer(_ => UiThread.Post(ReadActivityAgain), null, AgesReadEvery, AgesReadEvery);

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await _sys.CaptureAsync();
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowVitals(snapshot));

                    UpdateGpuUsage();
                    await Task.Delay(300, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // Broad on purpose: the live poll runs for as long as the tab is shown, and a tick that fails in
                    // any way waits a second and goes on.
                    Log.Debug(ex, "Dashboard polling error");
                    await Task.Delay(1000, ct);
                }
            }
        }, ct);
    }

    /// <summary>How often Recent activity is read again while the tab is shown.</summary>
    internal static readonly TimeSpan AgesReadEvery = TimeSpan.FromMinutes(1);

    /// <summary>Recent activity read again, for its ages, unless the tab was hidden since the timer was set.</summary>
    private void ReadActivityAgain()
    {
        if (IsActive) LoadActivity();
    }

    /// <summary>
    /// What one sample of the live poll shows: the processor, the memory and the uptime.
    /// </summary>
    /// <remarks>
    /// The uptime line was set once, from the start's own snapshot, although every sample carries it, so it stood
    /// still while the Dashboard stayed open (#2802).
    /// </remarks>
    internal void ShowVitals(SystemSnapshot snapshot)
    {
        CpuPercent = snapshot.Cpu.LoadPercent;
        RamPercent = snapshot.Memory.UsedPercent;
        RamUsedGB = snapshot.Memory.UsedGB;
        RamTotalGB = snapshot.Memory.TotalGB;
        RamAvailableGB = snapshot.Memory.AvailableGB;
        UptimeLine = UptimeText(snapshot.Os.Uptime);
    }

    /// <summary>The uptime line under the title.</summary>
    internal static string UptimeText(TimeSpan uptime) => $"Uptime {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";

    /// <summary>
    /// Shows the busiest GPU's load, name and memory, or the adapter's name alone when nothing can measure it.
    /// </summary>
    /// <remarks>
    /// <para>The reading is vendor-neutral now: Windows' own GPU counters, the ones Task Manager draws, so an
    /// Intel or AMD adapter reports a real number instead of the 0% an NVIDIA-only reading left behind (#2857).
    /// The busiest adapter is shown rather than the first, which on a laptop with switchable graphics is
    /// whichever card the foreground app actually landed on.</para>
    /// <para>When there is NO reading — no WDDM 2.x counters, or a machine between driver states — the load goes
    /// to null and the tile says "N/A" rather than a confident 0%, and the name falls back to the one Windows
    /// reports, which is static for the session and so costs one WMI query rather than one per tick.</para>
    /// </remarks>
    private void UpdateGpuUsage()
    {
        var gpus = _gpu?.ReadUsage() ?? [];
        if (gpus.Count > 0)
        {
            var gpu = gpus[0];
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                GpuPercent = gpu.UsagePercent;
                GpuName = gpu.Name;
                GpuVram = gpu.MemoryTotalGB > 0
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{gpu.MemoryUsedGB:F1} / {gpu.MemoryTotalGB:F1} GB VRAM")
                    : "";
            });
            return;
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => GpuPercent = null);
        if (!_gpuNameResolved)
            UpdateGpuNameFromWmi();
    }

    private void UpdateGpuNameFromWmi()
    {
        try
        {
            using var searcher = Wmi.Searcher("SELECT Name FROM Win32_VideoController");
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
            UptimeLine = UptimeText(snap.Os.Uptime);

            if (snap.Memory.Modules.Count > 0)
            {
                var firstModule = snap.Memory.Modules[0];
                RamType = $"{(firstModule.SpeedMHz > 0 ? $"DDR · {firstModule.SpeedMHz} MHz" : "")}";
            }
        }
        catch (Exception ex)
        {
            // Broad on purpose: the static card is filled once, at start, from WMI and the registry, and a failure of
            // any kind leaves it blank rather than the tab half-built.
            Log.Warning("Dashboard static info failed: {Error}", ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  STORAGE DRIVES
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fills the storage card, reading the drives off the UI thread, as Disk Analyzer and Duplicate Finder read theirs.
    /// </summary>
    /// <remarks>
    /// It read them on the UI thread, at start and on every Scan system, and asked every letter whether it was ready
    /// before its type ruled it out, so a mapped drive whose server could not be reached, or an optical drive spinning
    /// up, held the window until it gave up (#2802).
    /// </remarks>
    private async Task LoadDrivesAsync()
    {
        var drives = await Task.Run(ReadDrives).ConfigureAwait(true);
        if (drives is not null) Drives.ReplaceWith(drives);
    }

    /// <summary>The card's drives, or null when Windows could not list them, which leaves the card as it was.</summary>
    private static List<DriveUsageInfo>? ReadDrives()
    {
        try
        {
            return DriveUsage(DriveInfo.GetDrives().Select(DriveProbe.Of));
        }
        catch (IOException ex)
        {
            Log.Debug("Drive info failed: {Error}", ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug("Drive info refused: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// What the storage card shows for <paramref name="drives"/>: each fixed drive that is ready, with the space used
    /// and its size. One that cannot be read is left out, not the others (#2802).
    /// </summary>
    internal static List<DriveUsageInfo> DriveUsage(IEnumerable<DriveProbe> drives) =>
        DriveProbe.ReadFixed(drives, drive =>
        {
            var total = drive.TotalSize();
            var free = drive.FreeSpace();
            return new DriveUsageInfo(
                drive.Name.TrimEnd('\\'),
                (total - free) / 1024.0 / 1024.0 / 1024.0,
                total / 1024.0 / 1024.0 / 1024.0);
        });

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
        TemperaturesNeedDriver = readings.Any(r => r.RequiresDriver);

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            Temperatures.ReplaceWith(readings));
    }

    private async Task LoadTemperaturesAsync()
    {
        try { await RefreshTemperaturesAsync(); }
        catch (Exception ex)
        {
            // Broad on purpose: the sensors are read through LibreHardwareMonitor and NvAPI, both native, and a first
            // read that fails leaves the readings empty.
            Log.Debug("Temperature load failed: {Error}", ex.Message);
        }
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
                catch (Exception ex)
                {
                    // Broad on purpose: the sensor poll runs for as long as the tab is shown, and a read that fails in
                    // any way is skipped.
                    Log.Debug("Temp polling error: {Error}", ex.Message);
                }
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
    /// <para><paramref name="atStartup"/> is the round the launch starts, the one About can keep from asking winget
    /// (#2613). The other two rounds are asked for, so they check app updates whatever the switch says.</para>
    /// </remarks>
    private Task StartAlertScans(bool atStartup = false)
    {
        Alerts.Clear();

        // A setting that could not be read keeps the check from running, as it does the version check (#2614).
        var askWinget = !atStartup || UpdateCheckPreferenceService.ShouldCheckAppUpdatesAtStartup(_updateCheck.Load());

        var smartAlert = new DashboardAlert { Title = "Checking disk health...", State = AlertLoadingState.Loading };
        var appUpdateAlert = askWinget
            ? new DashboardAlert { Title = "Checking app updates...", State = AlertLoadingState.Loading }
            : AppUpdatesNotCheckedAtStartup();
        var memoryAlert = new DashboardAlert { Title = "Checking memory health...", State = AlertLoadingState.Loading };
        // Its button opens System Logs on the events its scan counts (#2887).
        var eventLogAlert = new DashboardAlert
        {
            Title = "Checking Event Log...",
            State = AlertLoadingState.Loading,
            Arrival = LogsArrival.CriticalSystemEvents,
        };
        var rebootAlert = new DashboardAlert { Title = "Checking for a pending reboot...", State = AlertLoadingState.Loading };

        Alerts.Add(smartAlert);
        Alerts.Add(appUpdateAlert);
        Alerts.Add(memoryAlert);
        Alerts.Add(eventLogAlert);
        Alerts.Add(rebootAlert);

        var scans = new List<Task>
        {
            RunAlertScanAsync(smartAlert, ScanSmartHealthAsync),
            askWinget ? RunAlertScanAsync(appUpdateAlert, ScanAppUpdatesAsync) : Task.CompletedTask,
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
            // Broad on purpose: each alert's check is its own function, and one that fails in any way marks that alert
            // rather than the scan.
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
            count = CountForTheAlert(upgradable);
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

    /// <summary>What the app-updates alert reads when About keeps its check at startup from running (#2613).</summary>
    internal const string AppUpdatesNotCheckedTitle = "App updates not checked at startup";

    /// <summary>
    /// The app-updates alert when its check at startup did not run: switched off in About, or its setting could not
    /// be read (#2613).
    /// </summary>
    /// <remarks>
    /// Grey, the colour of nothing to judge: green would say "all up to date" without having looked, and leaving the
    /// line out would make the card quietly check one thing fewer. Complete from the start, so it never shows the
    /// "Checking…" of a check that is not going to happen. Its button opens App Updates, where the check is one button
    /// away, and is labelled for that, since nothing here is a fault to fix.
    /// </remarks>
    internal static DashboardAlert AppUpdatesNotCheckedAtStartup() => new()
    {
        Title = AppUpdatesNotCheckedTitle,
        Severity = AlertSeverity.Neutral,
        State = AlertLoadingState.Complete,
        NavTargetId = "nav-app-updates",
        ActionLabel = "Open App Updates",
    };

    /// <summary>
    /// The updates the alert counts: those winget can compare with what is installed. An app whose installed version
    /// winget cannot read is offered on every scan, so counting it kept the alert yellow for good, and Update All Apps,
    /// which leaves those apps out, could never clear it.
    /// </summary>
    internal static int CountForTheAlert(IEnumerable<AppPackage> upgradable) => upgradable.Count(p => !p.HasUnknownVersion);

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

    private async Task ScanEventLogAsync(DashboardAlert alert)
    {
        // Counted by EventLogService, which the Logs tab reads with (#2802). The alert's own query compared local time
        // with the UTC Windows keeps, so "the last 7 days" moved by the UTC offset, and it put the level in the native
        // filter, which EventLogService explains can block a read for minutes.
        var tally = await _eventLog.TallyAsync(CriticalEventsQuery(DateTime.Now), CancellationToken.None)
            .ConfigureAwait(false);

        var (title, severity) = ClassifyEventLog(CriticalCount(tally));
        UiThread.Post(() =>
        {
            alert.Title = title;
            alert.Severity = severity;
            // Logs, not System Health. This line was written twice with different tabs and the
            // second won, so the button read as "take me to the events" and opened a page that does
            // not list them. The title names the Event Log, so that is where it has to go (#2359).
            alert.NavTargetId = NavTargetFor(severity, "nav-logs");
        });
    }

    /// <summary>
    /// What the Event Log alert counts at <paramref name="now"/>: the log, severities and days the alert's button opens
    /// System Logs on, so the count and that list cannot disagree (#2887). The service bounds the days in UTC.
    /// </summary>
    internal static EventLogQueryOptions CriticalEventsQuery(DateTime now)
    {
        var shown = LogsArrival.CriticalSystemEvents;
        return new EventLogQueryOptions
        {
            LogName = shown.Log,
            Severities = [.. shown.Severities],
            Since = now - shown.Within,
        };
    }

    /// <summary>
    /// The critical events a count found, or null when the log could not be read or reading was refused.
    /// </summary>
    internal static int? CriticalCount(EventLogService.EventTally tally) =>
        tally.Outcome == EventLogService.ReadOutcome.Ok ? tally.Groups.Sum(group => group.Count) : null;

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
        var (title, severity) = ClassifyPendingReboot(PendingReboot.Reasons());
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
    /// The reasons are the ones <see cref="PendingReboot.Reasons"/> reads, which the Windows Update tab shows too; the
    /// alert read only Windows Update's key (#2802). Null means the registry could not be read. That is yellow, not the
    /// green "Feature check unavailable" it used to be (#2479): only a read that found nothing reaches green.
    /// </remarks>
    internal static (string Title, AlertSeverity Severity) ClassifyPendingReboot(IReadOnlyList<string>? reasons) =>
        reasons switch
        {
            null => ("Pending reboot could not be checked", AlertSeverity.Yellow),
            [] => ("No pending reboots", AlertSeverity.Green),
            _ => ($"Pending reboot required ({string.Join(", ", reasons)})", AlertSeverity.Yellow),
        };

    // ══════════════════════════════════════════════════════════════════════
    //  RECENT ACTIVITY
    // ══════════════════════════════════════════════════════════════════════

    private void LoadActivity()
    {
        RecentActivity.ReplaceWith(ActivityLogService.Instance.GetRecent(5));
    }

    /// <summary>The newest Recent activity entry, or null when there is none.</summary>
    private static ActivityEntry? NewestActivity() =>
        ActivityLogService.Instance.GetRecent(1) is [var newest] ? newest : null;

    /// <summary>
    /// Reads the storage card and the health score again, after something may have changed how full a drive is.
    /// </summary>
    /// <remarks>
    /// Both were read only at start and on Scan system, so after Quick Cleanup or Quick Tune-Up said "Freed 3.2 GB"
    /// the bars had not moved and the score still advised freeing space, as though the cleanup had done nothing
    /// (#2802).
    /// </remarks>
    private Task RefreshStorageAsync() => StorageRead = ReadStorageAsync();

    private async Task ReadStorageAsync()
    {
        _newestAtStorageRead = NewestActivity();
        await LoadDrivesAsync();
        await LoadHealthScoreAsync();
    }

    /// <summary>
    /// The most recent read of the drives and the score after the start. It completes when both are read.
    /// </summary>
    /// <remarks>
    /// Nothing waits for the one coming back to the tab starts. A test awaits this to observe it, as
    /// <see cref="AlertScans"/> exposes a round of alerts.
    /// </remarks>
    internal Task StorageRead { get; private set; } = Task.CompletedTask;

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
                "Delete temporary files more than a week old from your user and Windows Temp folders?\n\n" +
                "Newer ones, which a program being installed may still need, and files in use are left alone. " +
                "This cannot be undone.",
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
            var (freed, _, _) = await _tuneUp.CleanTempFilesAsync(_quickActionCts!.Token);

            QuickActionProgress = 100;
            var freedMB = freed / 1024.0 / 1024.0;
            QuickActionDetail = freedMB >= 1024
                ? string.Create(CultureInfo.InvariantCulture, $"Freed {freedMB / 1024:F1} GB")
                : $"Freed {freedMB:F0} MB";
            ActivityLogService.Instance.Log("Quick Cleanup", QuickActionDetail);
        }, readsStorageAfter: true);
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
            // Upgrades one package at a time internally, checking the token between each, so a cancel lands
            // between packages rather than only at the very end (#2859).
            var result = await _winget.UpgradeAllAsync(_quickActionCts!.Token);
            QuickActionProgress = 100;
            ActivityLogService.Instance.Log("App Updates",
                result.Succeeded
                    ? "Upgrade all completed" + RestartNote(result)
                    : $"Upgrade all: {result.FriendlyMessage}");
            // Checked again whether or not every package upgraded, because either way the number the app update
            // alert showed is out of date, and it used to stay beside "All apps updated" (#2479).
            _ = StartAlertScans();
            // RunQuickActionAsync reports "✓ Done" for any action that returns, so a failed run must not return:
            // `winget upgrade --all` exits non-zero when any one package fails, and the card used to read "✓ Done"
            // above the failure text (#2437). Its failure branch shows the message as the detail.
            if (!result.Succeeded)
                throw new InvalidOperationException(result.FriendlyMessage);
            QuickActionDetail = "All apps updated" + RestartNote(result);
        });
    }

    // An app that needs Windows restarted to finish is updated, and the card says to restart rather than that the
    // run failed (#2849).
    private static string RestartNote(WingetResult result) =>
        result.RestartRequired ? " — restart Windows to finish" : "";

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
                found = await _windowsUpdate.ScanAsync(_quickActionCts!.Token);
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
                reporter => _speedTest.RunHttpAsync(reporter, _quickActionCts!.Token));

            QuickActionProgress = 100;
            QuickActionDetail = DescribeSpeedTest(result, saved: await _speedHistory.SaveAsync(result));
        }, hasRealProgress: true);
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

    /// <summary>Opens the tab an alert names, on what the alert counted when it carries that (#2887).</summary>
    [RelayCommand]
    private void OpenAlert(DashboardAlert? alert)
    {
        if (alert is { CanNavigate: true }) _navigation.GoTo(alert.NavTargetId, arrival: alert.Arrival);
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

    /// <summary>Stops the quick action in progress. A no-op once it has finished (#2859).</summary>
    [RelayCommand]
    private void CancelQuickAction() => _quickActionCts?.Cancel();

    private bool CanRunQuickAction() => !IsQuickActionRunning || IsQuickActionDone;

    /// <summary>
    /// Runs one of the Dashboard's quick actions on its card, which says it is running and then that it is done or
    /// why it failed, with a link to the tab that has more.
    /// </summary>
    /// <param name="readsStorageAfter">
    /// Whether the action changes how full a drive is, so the storage card and the score are read again once it is
    /// done.
    /// </param>
    /// <param name="hasRealProgress">
    /// Whether <paramref name="action"/> reports a genuinely continuous percentage (only Speed Test does).
    /// The other three only ever have two fixed milestones, which an indeterminate bar represents honestly
    /// and a determinate one does not (#2859).
    /// </param>
    private async Task RunQuickActionAsync(
        string name, string tabLabel, string navId, Func<Task> action, bool readsStorageAfter = false,
        bool hasRealProgress = false)
    {
        _quickActionCts = new CancellationTokenSource();
        IsQuickActionRunning = true;
        IsQuickActionDone = false;
        IsQuickActionIndeterminate = !hasRealProgress;
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

        var done = false;
        try
        {
            await action();
            QuickActionStatus = "✓ Done";
            IsQuickActionDone = true;
            LoadActivity();
            done = true;
        }
        catch (OperationCanceledException)
        {
            QuickActionStatus = "Cancelled";
            IsQuickActionDone = true;
        }
        catch (Exception ex)
        {
            // Broad on purpose: a quick action is any of the tab's operations, and whatever one throws is shown on its
            // card instead of escaping the command.
            Log.Debug(ex, "Quick action {Name} failed", name);
            QuickActionStatus = "Failed";
            QuickActionDetail = ex.Message;
            IsQuickActionDone = true;
        }
        finally
        {
            _quickActionCts?.Dispose();
            _quickActionCts = null;
            QuickCleanupCommand.NotifyCanExecuteChanged();
            QuickUpdateAppsCommand.NotifyCanExecuteChanged();
            QuickWindowsUpdateCommand.NotifyCanExecuteChanged();
            QuickSpeedTestCommand.NotifyCanExecuteChanged();
        }

        // After the card says it is done, so the action reads as finished while the score is computed.
        if (done && readsStorageAfter) await RefreshStorageAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  COMMANDS
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _refreshing = true;
        FollowJobs();
        StatusMessage = "Scanning...";
        try
        {
            await LoadStaticInfoAsync();
            _newestAtStorageRead = NewestActivity();
            await LoadDrivesAsync();
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
        finally
        {
            _refreshing = false;
            FollowJobs();
        }
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
        var tunedUp = false;

        try
        {
            TuneUpResult = await _tuneUp.RunAsync(true, progress, _tuneUpCts.Token);
            HasTuneUpResult = true;
            // Everything it freed, the Recycle Bin included. These reported the temp files alone, so a Tune-Up that
            // permanently emptied gigabytes from the bin left nothing in Recent activity to say so.
            StatusMessage = $"Tune-Up complete — {TuneUpResult.TotalFreedDisplay} freed";
            ToastService.Instance.Show("Tune-Up complete", $"{TuneUpResult.TotalFreedDisplay} freed");
            ActivityLogService.Instance.Log("Quick Tune-Up", TuneUpResult.ActivityDetail);
            LoadActivity();
            tunedUp = true;
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

        // Once the Tune-Up reads as finished: what it freed shows on the storage card and in the score (#2802).
        if (tunedUp) await RefreshStorageAsync();
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

    // ── The sidebar and the taskbar follow every job on this tab (#2596) ──

    /// <summary>True while the scan behind Refresh runs.</summary>
    private bool _refreshing;

    /// <summary>
    /// Marks the tab busy while any of its jobs runs, and shows the running job's figure on the taskbar button.
    /// </summary>
    /// <remarks>
    /// The sidebar's busy mark and the taskbar button follow <see cref="ViewModelBase.IsBusy"/>, and only Refresh
    /// set it, so Quick Tune-Up, a quick action and the slowdown check ran with neither showing anything. Quick
    /// Tune-Up and a quick action report a figure of their own; the slowdown check reports none, so it only marks
    /// the tab busy. A quick action's card stays on screen once it is done, until it is dismissed, so done is not
    /// running.
    /// </remarks>
    private void FollowJobs()
    {
        var quickActionRuns = IsQuickActionRunning && !IsQuickActionDone;
        IsBusy = _refreshing || IsTuneUpRunning || quickActionRuns || IsSlowdownCheckRunning || IsHealthScoreLoading;
        ShowOnTaskbar(IsTuneUpRunning ? TuneUpProgress : quickActionRuns ? QuickActionProgress : 0);
    }

    partial void OnIsTuneUpRunningChanged(bool value) => FollowJobs();

    // The score is computed at start-up too, outside a refresh, and the tab was not busy while it ran there (#2876).
    partial void OnIsHealthScoreLoadingChanged(bool value) => FollowJobs();
    partial void OnTuneUpProgressChanged(int value) => FollowJobs();
    partial void OnIsQuickActionRunningChanged(bool value) => FollowJobs();
    partial void OnIsQuickActionDoneChanged(bool value) => FollowJobs();
    partial void OnQuickActionProgressChanged(int value) => FollowJobs();
    partial void OnIsSlowdownCheckRunningChanged(bool value) => FollowJobs();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollingCts?.Cancel();
            _pollingCts?.Dispose();
            _agesTimer?.Dispose();
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
