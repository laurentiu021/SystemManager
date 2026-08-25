// SysManager · MainWindowViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    // The DI container (runtime) or null (designer/tests). When present, tab VMs are
    // resolved lazily — each tab's NavItem builds its VM on first open (see NavItem.Content).
    private readonly IServiceProvider? _sp;

    // Shared network state — injected into all four network tab VMs and owning SkiaSharp paint
    // handles, so it is created once here (not per tab) and disposed explicitly below. It starts
    // no background polling on construction, so keeping it eager costs nothing at startup.
    private readonly NetworkSharedState? _networkShared;

    // Kept eager (must exist at startup, independent of whether their tab is ever opened):
    //  • Dashboard — it is the initially-selected tab, so it is built immediately anyway.
    //  • DarkMode  — owns the always-on dark/light theme SCHEDULE poll; nothing else runs it, so
    //                if it were lazy a user's schedule would silently stop until they opened the tab.
    //  • About     — its constructor runs the startup update-check that drives the app-shell
    //                update banner (MainWindow binds About.UpdateAvailable / .CurrentVersion). If it
    //                were lazy, the banner and version label would stay blank until the tab was opened.
    private readonly DashboardViewModel? _dashboard;

    /// <summary>
    /// The About tab's view-model. Exposed as a property (not just a NavItem) because the app
    /// shell (MainWindow.xaml) binds its version label and update banner to it, and its
    /// constructor runs the startup update-check that populates those bindings.
    /// </summary>
    public AboutViewModel About { get; }

    /// <summary>Grouped sidebar tree (12 categories).</summary>
    public ObservableCollection<NavGroup> NavGroups { get; } = new();

    /// <summary>Flat list of every leaf NavItem (backward compat + lookup).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private string _title = "SysManager";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _elevationBadge = "";

    private bool _disposed;

    /// <summary>
    /// Whether the main window is on screen. A tab's poll loop runs only while it is BOTH selected
    /// and visible: closing to the tray hides the window without deselecting anything, so gating on
    /// selection alone left the open tab sampling at 1 Hz for as long as the PC stayed on.
    /// <para>Defaults to true because the window is shown before anything sets this, and the
    /// pre-existing behaviour for a visible window must not change.</para>
    /// </summary>
    public bool IsWindowVisible
    {
        get => _isWindowVisible;
        set
        {
            if (_isWindowVisible == value) return;
            _isWindowVisible = value;
            OnPropertyChanged();
            ApplyPollGate(SelectedNav, value);
        }
    }

    private bool _isWindowVisible = true;

    /// <summary>
    /// Applies the poll gate to <paramref name="item"/>: its loop runs only when the window is on
    /// screen AND the tab is the selected one.
    /// <para>Only touches a tab whose view-model was actually built — a never-opened lazy tab has
    /// nothing polling, and reading <see cref="NavItem.Content"/> would construct it, undoing the
    /// lazy-startup fix. Static and internal so the gate can be tested without the whole shell,
    /// which cannot be constructed in the unit suite (it runs About's startup update check).</para>
    /// </summary>
    internal static void ApplyPollGate(NavItem? item, bool windowVisible)
    {
        if (item is { IsContentCreated: true } created)
            SetActive(created.Content, windowVisible);
    }

    /// <summary>
    /// Parameterless constructor — used by XAML designer and tests.
    /// At runtime (DI container available) tab VMs are resolved LAZILY: each NavItem builds its
    /// view-model on first open, so the ~40 tabs that kick off a background scan/timer in their
    /// constructor no longer all run at startup. When DI is unavailable (tests/designer), every VM
    /// is created eagerly and handed to its NavItem as before.
    /// </summary>
    public MainWindowViewModel()
    {
        _sp = App.Services;
        if (_sp is null)
        {
            // Designer / test path: no container → build the whole VM graph eagerly up front.
            _designerVms = BuildDesignerGraph();
        }

        // NetworkSharedState is shared by the four network tabs and disposed explicitly below.
        _networkShared = Eager<NetworkSharedState>();
        // Dashboard is the initially-selected tab (built immediately regardless).
        _dashboard = Eager<DashboardViewModel>();
        // DarkMode is resolved eagerly so its always-on theme schedule poll starts with the app,
        // independent of whether the user ever opens that tab.
        _ = Eager<DarkModeViewModel>();
        // About is resolved eagerly so its constructor's startup update-check runs immediately —
        // the app-shell update banner and version label bind to it (see the About property above).
        About = Eager<AboutViewModel>();

        InitNavigation();
    }

    private void InitNavigation()
    {
        IsElevated = AdminHelper.IsElevated();
        ElevationBadge = IsElevated ? "Administrator" : "Standard user";
        Title = IsElevated ? "SysManager — Administrator" : "SysManager";
        Log.Information("MainWindow initialized. Elevated: {IsElevated}", IsElevated);

        foreach (var g in BuildNavGroups())
        {
            NavGroups.Add(g);
            foreach (var item in g.Children)
            {
                item.WireBusy(); // no-op for lazy items until their VM is first materialised
                NavItems.Add(item);
            }
        }

        SelectedNav = NavItems[0];
    }

    // ── Tab factories ───────────────────────────────────────────────────────
    // At runtime each returns a lazy NavItem (VM resolved from DI on first open). In the
    // designer/test path (no container) they build the VM eagerly and register it for disposal.

    private NavItem Tab<TVm>(string id, string label, Type viewType, bool inDevelopment = false)
        where TVm : class
    {
        if (_sp is not null)
        {
            return new NavItem
            {
                Id = id,
                Label = label,
                Glyph = "",
                ViewType = viewType,
                IsInDevelopment = inDevelopment,
                ContentFactory = () => _sp.GetRequiredService<TVm>(),
            };
        }
        // Designer/test path: no container → build eagerly from the manual graph.
        var vm = _designerVms![typeof(TVm)];
        return EagerItem(id, label, viewType, vm, inDevelopment);
    }

    // An eagerly-provided VM (Dashboard and the designer/test graph). The instance is set as the
    // NavItem's Content, so NavItem.Dispose disposes it on teardown like any other tab.
    private static NavItem EagerItem(string id, string label, Type viewType, object content, bool inDevelopment = false)
        => new()
        {
            Id = id,
            Label = label,
            Glyph = "",
            ViewType = viewType,
            Content = content,
            IsInDevelopment = inDevelopment,
        };

    // Resolve an eager VM: from DI when available, else from the designer graph.
    private T Eager<T>() where T : class =>
        _sp is not null ? _sp.GetRequiredService<T>() : (T)_designerVms![typeof(T)];

    private NavGroup[] BuildNavGroups() =>
    [
        Group("grp-dashboard", "Dashboard",
            EagerItem("nav-dashboard", "Dashboard", typeof(Views.DashboardView), _dashboard ?? Eager<DashboardViewModel>())),

        Group("grp-system", "System",
            Tab<SystemHealthViewModel>("nav-system-health",    "System Health",    typeof(Views.SystemHealthView)),
            Tab<WindowsUpdateViewModel>("nav-windows-update",  "Windows Update",   typeof(Views.WindowsUpdateView)),
            Tab<PerformanceViewModel>("nav-performance",       "Performance Mode", typeof(Views.PerformanceView)),
            Tab<ServicesViewModel>("nav-services",             "Services",         typeof(Views.ServicesView)),
            Tab<StartupViewModel>("nav-startup",               "Startup Manager",  typeof(Views.StartupView)),
            Tab<WindowsFeaturesViewModel>("nav-windows-features", "Windows Features", typeof(Views.WindowsFeaturesView)),
            Tab<RestorePointsViewModel>("nav-restore-points",  "Restore Points",   typeof(Views.RestorePointsView)),
            Tab<TaskSchedulerViewModel>("nav-task-scheduler",  "Task Scheduler",   typeof(Views.TaskSchedulerView)),
            Tab<BootAnalyzerViewModel>("nav-boot-analyzer",    "Boot Analyzer",    typeof(Views.BootAnalyzerView)),
            Tab<SystemFixesViewModel>("nav-system-fixes",      "System Fixes",     typeof(Views.SystemFixesView)),
            Tab<TweaksHubViewModel>("nav-tweaks-hub",          "Tweaks Hub",       typeof(Views.TweaksHubView), inDevelopment: true)),

        Group("grp-gaming", "Gaming & Profiles",
            Tab<GamingProfileViewModel>("nav-gaming-profile",   "Gaming Profile",       typeof(Views.GamingProfileView), inDevelopment: true),
            Tab<StandbyMemoryViewModel>("nav-standby-cleaner",  "Standby List Cleaner", typeof(Views.StandbyMemoryView)),
            Tab<TimerResolutionViewModel>("nav-timer-resolution", "Timer Resolution",   typeof(Views.TimerResolutionView)),
            Tab<CpuAffinityViewModel>("nav-cpu-affinity",       "CPU Core Affinity",    typeof(Views.CpuAffinityView)),
            Tab<DisplayProfileViewModel>("nav-display-profiles", "Display Profiles",    typeof(Views.DisplayProfileView))),

        Group("grp-monitor", "Monitor",
            Tab<ProcessManagerViewModel>("nav-processes",       "Process Manager",    typeof(Views.ProcessManagerView)),
            Tab<ResourceHistoryViewModel>("nav-resource-history", "Resource History", typeof(Views.ResourceHistoryView), inDevelopment: true),
            Tab<PrivacyMonitorViewModel>("nav-privacy-monitor", "Camera/Mic/Location", typeof(Views.PrivacyMonitorView)),
            Tab<AppAlertsViewModel>("nav-app-alerts",           "New App Alerts",     typeof(Views.AppAlertsView)),
            Tab<FileLockViewModel>("nav-file-lock",             "File Lock Detector", typeof(Views.FileLockView)),
            Tab<SettingsWatchdogViewModel>("nav-settings-watchdog", "Settings Watchdog", typeof(Views.SettingsWatchdogView), inDevelopment: true),
            Tab<BandwidthMonitorViewModel>("nav-bandwidth-monitor", "Bandwidth Monitor", typeof(Views.BandwidthMonitorView))),

        Group("grp-cleanup", "Cleanup",
            Tab<CleanupViewModel>("nav-cleanup",                     "Quick Cleanup",         typeof(Views.CleanupView)),
            Tab<DeepCleanupViewModel>("nav-deep-cleanup",            "Deep Cleanup",          typeof(Views.DeepCleanupView)),
            Tab<ShortcutCleanerViewModel>("nav-shortcut-cleaner",    "Shortcut Cleaner",      typeof(Views.ShortcutCleanerView)),
            Tab<ScheduledMaintenanceViewModel>("nav-scheduled-maintenance", "Scheduled Maintenance", typeof(Views.ScheduledMaintenanceView), inDevelopment: true)),

        Group("grp-storage", "Storage",
            Tab<DiskAnalyzerViewModel>("nav-disk-analyzer", "Disk Analyzer",    typeof(Views.DiskAnalyzerView)),
            Tab<DuplicateFileViewModel>("nav-duplicates",   "Duplicate Finder", typeof(Views.DuplicateFileView))),

        Group("grp-network", "Network",
            Tab<PingViewModel>("nav-ping",                   "Ping",           typeof(Views.PingView)),
            Tab<TracerouteViewModel>("nav-traceroute",       "Traceroute",     typeof(Views.TracerouteView)),
            Tab<SpeedTestViewModel>("nav-speed-test",        "Speed Test",     typeof(Views.SpeedTestView)),
            Tab<NetworkRepairViewModel>("nav-network-repair", "Network Repair", typeof(Views.NetworkRepairView)),
            Tab<DnsHostsViewModel>("nav-dns-hosts", "DNS & Hosts", typeof(Views.DnsHostsView))),

        Group("grp-apps", "Apps",
            Tab<AppUpdatesViewModel>("nav-app-updates",    "App Updates",    typeof(Views.AppUpdatesView)),
            Tab<BulkInstallerViewModel>("nav-bulk-installer", "Bulk Installer", typeof(Views.BulkInstallerView)),
            Tab<UninstallerViewModel>("nav-uninstaller",   "Uninstaller",    typeof(Views.UninstallerView))),

        Group("grp-privacy", "Privacy & Security",
            Tab<PrivacyViewModel>("nav-privacy-settings",  "Privacy & Telemetry",   typeof(Views.PrivacyView)),
            Tab<FileShredderViewModel>("nav-file-shredder", "File Shredder",         typeof(Views.FileShredderView)),
            Tab<AppBlockerViewModel>("nav-app-blocker",     "App Blocker",           typeof(Views.AppBlockerView)),
            Tab<DebloaterViewModel>("nav-debloater",        "Debloater & Ads",       typeof(Views.DebloaterView)),
            Tab<BrowserCleanerViewModel>("nav-browser-cleaner", "Browser Cleaner",   typeof(Views.BrowserCleanerView)),
            Tab<EdgeOneDriveViewModel>("nav-edge-onedrive", "Edge/OneDrive Remover", typeof(Views.EdgeOneDriveView)),
            Tab<DefenderViewModel>("nav-defender-tweaks",   "Defender Tweaks",       typeof(Views.DefenderView)),
            Tab<NotificationBlockerViewModel>("nav-notification-blocker", "Notification Blocker", typeof(Views.NotificationBlockerView), inDevelopment: true)),

        Group("grp-customization", "Customization",
            Tab<ContextMenuViewModel>("nav-context-menu",   "Context Menu",          typeof(Views.ContextMenuView)),
            // DarkMode is eager (schedule poll must run app-wide); hand the DI singleton to its NavItem.
            EagerItem("nav-dark-mode", "Dark Mode Scheduler", typeof(Views.DarkModeView), Eager<DarkModeViewModel>()),
            Tab<AudioMixerViewModel>("nav-volume-control",  "Volume Control",        typeof(Views.AudioMixerView))),

        Group("grp-info", "Info",
            Tab<DriversViewModel>("nav-drivers",       "Drivers",        typeof(Views.DriversView)),
            Tab<BatteryHealthViewModel>("nav-battery", "Battery Health", typeof(Views.BatteryHealthView)),
            Tab<LogsViewModel>("nav-logs",             "System Logs",    typeof(Views.LogsView)),
            Tab<SystemReportViewModel>("nav-system-report", "System Report", typeof(Views.SystemReportView)),
            Tab<LegacyPanelsViewModel>("nav-legacy-panels", "Legacy Panels", typeof(Views.LegacyPanelsView)),
            // About is eager (its startup update-check drives the shell banner); the tab reuses
            // that same instance so the sidebar version label and the tab show one shared VM.
            EagerItem("nav-about", "About", typeof(Views.AboutView), About)),

        Group("grp-advanced", "Advanced",
            Tab<ProfileViewModel>("nav-profile-export", "Profile Export / Import", typeof(Views.ProfileView)),
            Tab<CliInterfaceViewModel>("nav-cli-interface", "CLI Interface",     typeof(Views.CliInterfaceView), inDevelopment: true),
            Tab<EnvironmentVariablesViewModel>("nav-env-variables", "Environment Variables", typeof(Views.EnvironmentVariablesView))),
    ];

    private static NavGroup Group(string id, string label, params NavItem[] children)
    {
        var g = new NavGroup { Id = id, Label = label, Glyph = "" };
        foreach (var c in children) g.Children.Add(c);
        g.Subtitle = string.Join(" · ", children.Select(c => c.Label));
        g.Tooltip = string.Join("\n", children.Select(c => c.Label));
        return g;
    }

    partial void OnSelectedNavChanged(NavItem? oldValue, NavItem? newValue)
    {
        UpdateSelectionState(oldValue, newValue);

        if (newValue is null)
        {
            if (oldValue is { IsContentCreated: true }) SetActive(oldValue.Content, false);
            return;
        }

        // Navigation is recorded here (Serilog) and NOT in the activity log. It used to write an
        // "Opened <tab>" entry per navigation, which — against a 20-entry cap — meant a few minutes of
        // clicking evicted every real action, so the Dashboard's "Recent activity" card listed tab
        // visits while omitting the deletes it should have shown. The card now answers "what did this
        // app change on my PC?" rather than "where have I clicked?".
        Log.Information("Tab navigated: {TabLabel}", newValue.Label);

        // Auto-expand the parent group when a child is selected.
        var parentGroup = NavGroups.FirstOrDefault(g => g.Children.Contains(newValue));
        if (parentGroup is not null) parentGroup.IsExpanded = true;

        // Pause/resume per-tab poll loops based on visibility (reconcile loops + the volume
        // mixer's peak-meter timer only run while their tab is on screen). Deactivate the tab
        // we left and activate the one we entered — but only touch a tab's VM if it was actually
        // built (a never-opened lazy tab has no VM and, by definition, nothing polling).
        if (oldValue is { IsContentCreated: true }) SetActive(oldValue.Content, false);
        // Gate on visibility as well: the tray menu can navigate while the window is hidden (see
        // NavigateTo), and starting a loop nothing can see is the very cost this gate exists to avoid.
        SetActive(newValue.Content, IsWindowVisible); // accessing Content materialises the entered tab's VM
    }

    internal static void UpdateSelectionState(NavItem? oldValue, NavItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    // Every tab with a visibility-gated poll exposes an IsActive flag. Toggle it generically so this
    // doesn't depend on eager VM properties that no longer exist for lazy tabs.
    // internal (not private) so a test can pin the gate without constructing the whole shell,
    // exactly as UpdateSelectionState above is tested.
    //
    // This list is hand-maintained, which means a NEW polling tab is opted OUT of the gate by default
    // and nothing about the omission looks wrong — that is how Standby List Cleaner ended up polling
    // every 2 seconds for the whole session after being opened once. ArchitectureTests now asserts
    // every view model declaring IsActive appears here, so the next one cannot be forgotten silently.
    internal static void SetActive(object content, bool active)
    {
        switch (content)
        {
            case ProcessManagerViewModel pm: pm.IsActive = active; break;
            case DashboardViewModel db: db.IsActive = active; break;
            case AudioMixerViewModel am: am.IsActive = active; break;
            case BandwidthMonitorViewModel bw: bw.IsActive = active; break;
            case StandbyMemoryViewModel sm: sm.IsActive = active; break;
        }
    }

    /// <summary>Select a nav item by its automation id.</summary>
    private void SelectNavById(string id)
    {
        var item = NavItems.FirstOrDefault(n => n.Id == id);
        if (item is not null) SelectedNav = item;
    }

    /// <summary>
    /// Public navigation seam for out-of-tree callers (e.g. the system-tray "Volume mixer"
    /// shortcut). Selects the tab by its nav id; unknown ids are ignored.
    /// </summary>
    public void NavigateTo(string navId) => SelectNavById(navId);

    // Only the About shortcut is bound (MainWindow.xaml's update banner, "View details"). Five
    // sibling commands — OpenDeepCleanupTab, OpenDiskAnalyzerTab, OpenDuplicatesTab, OpenCleanupTab
    // and OpenSystemHealthTab — existed here with no binding and no test, so they were removed rather
    // than left looking like available navigation. Anything that needs to jump to a tab should use
    // NavigateTo(navId) above, or DashboardViewModel's OpenTabCommand for a bindable version; one
    // parameterised seam beats a per-tab command that has to be added by hand and can silently rot.
    [RelayCommand]
    private void OpenAboutTab() => SelectNavById("nav-about");

    public void Dispose()
    {
        // Idempotency guard: Dispose is wired to two shutdown paths (OnClosed and
        // Application.Exit), so it can run more than once. Disposing the SkiaSharp
        // paint handles (via NetworkShared) twice is undefined behavior.
        if (_disposed) return;
        _disposed = true;

        // Dispose each NavItem — this unsubscribes its IsBusy handler AND disposes the tab VM,
        // but ONLY for tabs that were actually opened (NavItem.Dispose no-ops on an un-built VM).
        // In the DI path the VMs are singletons; the container also disposes them at OnExit, but
        // ViewModelBase.Dispose is idempotent so the double call is safe.
        foreach (var item in NavItems)
            item.Dispose();

        // Shared network state is not a tab — dispose it explicitly (once).
        _networkShared?.Dispose();

        GC.SuppressFinalize(this);
    }

    // ── Designer / test dependency graph ────────────────────────────────────
    // Built lazily (and only when there is no DI container) so the parameterless ctor keeps
    // working in the XAML designer and unit tests exactly as before — every VM eager, no DI.
    private readonly Dictionary<Type, object>? _designerVms;

    private Dictionary<Type, object> BuildDesignerGraph()
    {
        var runner = new PowerShellRunner();
        var sysInfo = new SystemInfoService();
        var winget = new WingetService(runner);
        var diskHealth = new DiskHealthService();
        var battery = new BatteryService();
        var shortcuts = new ShortcutCleanerService();
        var tuneUp = new TuneUpService(shortcuts, diskHealth, sysInfo);
        var healthScore = new HealthScoreService(sysInfo, diskHealth, battery);
        var fixedDrives = new FixedDriveService();
        var pinger = new PingMonitorService();
        var tracer = new TracerouteService();
        var traceMonitor = new TracerouteMonitorService();
        var speedTest = new SpeedTestService();
        var netRepair = new NetworkRepairService(runner);
        // One shared instance, as in the container: the four network view models coordinate through
        // it (one tab's Start must disable the other's), so a second copy would silently split them.
        var networkShared = new NetworkSharedState(pinger, tracer, traceMonitor, speedTest, netRepair);
        var restorePoints = new RestorePointService(runner);
        // ONE session restore point for the graph, as in DI: a second instance would let two
        // tabs each attempt a snapshot, and Windows refuses the second within 24h anyway.
        var sessionRestorePoint = new SessionRestorePoint(restorePoints.CreateAsync);
        var gamingCpu = new CpuAffinityService();
        // ONE gaming service for the whole graph. Performance Mode asks it whether a profile is live
        // before it records a recovery baseline, so a second copy would answer "no" while the first
        // one had a session running — which is exactly the state that must never be snapshotted.
        // Under DI both resolve the same singleton; this keeps the designer/test path honest.
        var gamingProfiles = new GamingProfileService(
            new PerformanceService(runner, restorePoints),
            new TimerResolutionService(), gamingCpu,
            new StandbyMemoryService(), sessionRestorePoint,
            Helpers.AdminHelper.IsElevated());

        return new Dictionary<Type, object>
        {
            [typeof(DashboardViewModel)] = new DashboardViewModel(sysInfo, tuneUp, healthScore, new TemperatureService(diskHealth), winget, new CrashMarkerService()),
            [typeof(AppUpdatesViewModel)] = new AppUpdatesViewModel(winget),
            [typeof(WindowsUpdateViewModel)] = new WindowsUpdateViewModel(runner, new WindowsUpdateService(), new WindowsUpdatePolicyService()),
            [typeof(SystemHealthViewModel)] = new SystemHealthViewModel(sysInfo, diskHealth, new MemoryTestService(), fixedDrives, runner, new BiosService()),
            [typeof(CleanupViewModel)] = new CleanupViewModel(runner),
            [typeof(DeepCleanupViewModel)] = new DeepCleanupViewModel(new DeepCleanupService(), new LargeFileScanner(), fixedDrives),
            [typeof(DuplicateFileViewModel)] = new DuplicateFileViewModel(new DuplicateFileService()),
            [typeof(DiskAnalyzerViewModel)] = new DiskAnalyzerViewModel(new DiskAnalyzerService()),
            [typeof(ProcessManagerViewModel)] = new ProcessManagerViewModel(new ProcessManagerService()),
            [typeof(BatteryHealthViewModel)] = new BatteryHealthViewModel(battery),
            [typeof(UninstallerViewModel)] = new UninstallerViewModel(new UninstallerService(runner)),
            [typeof(PerformanceViewModel)] = new PerformanceViewModel(new PerformanceService(runner, restorePoints), gamingProfiles),
            [typeof(StartupViewModel)] = new StartupViewModel(new StartupService()),
            [typeof(NetworkSharedState)] = networkShared,
            [typeof(PingViewModel)] = new PingViewModel(networkShared),
            [typeof(TracerouteViewModel)] = new TracerouteViewModel(networkShared),
            [typeof(SpeedTestViewModel)] = new SpeedTestViewModel(networkShared, new SpeedTestHistoryService()),
            [typeof(NetworkRepairViewModel)] = new NetworkRepairViewModel(networkShared),
            [typeof(DriversViewModel)] = new DriversViewModel(runner),
            [typeof(LogsViewModel)] = new LogsViewModel(new EventLogService()),
            [typeof(AboutViewModel)] = new AboutViewModel(),
            [typeof(ServicesViewModel)] = new ServicesViewModel(runner),
            [typeof(AppAlertsViewModel)] = new AppAlertsViewModel(new AppAlertService()),
            [typeof(ShortcutCleanerViewModel)] = new ShortcutCleanerViewModel(shortcuts),
            [typeof(AppBlockerViewModel)] = new AppBlockerViewModel(new AppBlockerService()),
            [typeof(BulkInstallerViewModel)] = new BulkInstallerViewModel(new BulkInstallerService(new PowerShellRunner()), new AppIconService()),
            [typeof(FileShredderViewModel)] = new FileShredderViewModel(new FileShredderService()),
            [typeof(DnsHostsViewModel)] = new DnsHostsViewModel(new DnsService(new PowerShellRunner()), new HostsFileService()),
            [typeof(WindowsFeaturesViewModel)] = new WindowsFeaturesViewModel(new WindowsFeaturesService(runner)),
            [typeof(PrivacyViewModel)] = new PrivacyViewModel(new PrivacyService(), sessionRestorePoint),
            [typeof(ContextMenuViewModel)] = new ContextMenuViewModel(new ContextMenuService()),
            [typeof(SystemReportViewModel)] = new SystemReportViewModel(new SystemReportService(sysInfo, diskHealth)),
            [typeof(EnvironmentVariablesViewModel)] = new EnvironmentVariablesViewModel(new EnvironmentVariableService()),
            [typeof(RestorePointsViewModel)] = new RestorePointsViewModel(restorePoints),
            [typeof(DebloaterViewModel)] = new DebloaterViewModel(new DebloaterService(new PowerShellRunner()), sessionRestorePoint),
            [typeof(EdgeOneDriveViewModel)] = new EdgeOneDriveViewModel(new EdgeOneDriveService(new PowerShellRunner()), sessionRestorePoint),
            [typeof(LegacyPanelsViewModel)] = new LegacyPanelsViewModel(new LegacyPanelService()),
            [typeof(SystemFixesViewModel)] = new SystemFixesViewModel(new SystemFixService(new PowerShellRunner())),
            [typeof(ProfileViewModel)] = new ProfileViewModel(new ProfileService()),
            [typeof(BrowserCleanerViewModel)] = new BrowserCleanerViewModel(new BrowserCleanerService()),
            [typeof(PrivacyMonitorViewModel)] = new PrivacyMonitorViewModel(new PrivacyMonitorService()),
            [typeof(BootAnalyzerViewModel)] = new BootAnalyzerViewModel(new BootAnalyzerService()),
            [typeof(TimerResolutionViewModel)] = new TimerResolutionViewModel(new TimerResolutionService()),
            [typeof(FileLockViewModel)] = new FileLockViewModel(new FileLockService()),
            [typeof(DisplayProfileViewModel)] = new DisplayProfileViewModel(new DisplayProfileService()),
            [typeof(CpuAffinityViewModel)] = new CpuAffinityViewModel(new CpuAffinityService()),
            [typeof(DefenderViewModel)] = new DefenderViewModel(new DefenderService(new PowerShellRunner()), sessionRestorePoint),
            [typeof(TaskSchedulerViewModel)] = new TaskSchedulerViewModel(new TaskSchedulerService(new PowerShellRunner())),
            [typeof(DarkModeViewModel)] = new DarkModeViewModel(new WindowsThemeService()),
            [typeof(StandbyMemoryViewModel)] = new StandbyMemoryViewModel(new StandbyMemoryService()),
            [typeof(ResourceHistoryViewModel)] = new ResourceHistoryViewModel(new ResourceHistoryService(sysInfo, new TemperatureService(diskHealth))),
            [typeof(BandwidthMonitorViewModel)] = new BandwidthMonitorViewModel(new BandwidthHistoryService()),
            [typeof(SettingsWatchdogViewModel)] = new SettingsWatchdogViewModel(new SettingsWatchdogService()),
            [typeof(CliInterfaceViewModel)] = new CliInterfaceViewModel(),
            [typeof(ScheduledMaintenanceViewModel)] = new ScheduledMaintenanceViewModel(new MaintenanceSchedulerService(new PowerShellRunner())),
            [typeof(TweaksHubViewModel)] = new TweaksHubViewModel(new TweaksHubService(new PrivacyService(), sessionRestorePoint)),
            [typeof(AudioMixerViewModel)] = new AudioMixerViewModel(new AudioMixerService(), new VolumePresetService()),
            [typeof(NotificationBlockerViewModel)] = new NotificationBlockerViewModel(new NotificationBlockerService()),
            [typeof(GamingProfileViewModel)] = new GamingProfileViewModel(gamingProfiles, gamingCpu),
        };
    }
}
