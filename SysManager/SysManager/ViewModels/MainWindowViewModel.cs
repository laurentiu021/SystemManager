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
    public DashboardViewModel Dashboard { get; }
    public AppUpdatesViewModel AppUpdates { get; }
    public WindowsUpdateViewModel WindowsUpdate { get; }
    public SystemHealthViewModel SystemHealth { get; }
    public CleanupViewModel Cleanup { get; }
    public DeepCleanupViewModel DeepCleanup { get; }
    public DuplicateFileViewModel DuplicateFile { get; }
    public DiskAnalyzerViewModel DiskAnalyzer { get; }
    public ProcessManagerViewModel ProcessManager { get; }
    public BatteryHealthViewModel BatteryHealth { get; }
    public UninstallerViewModel Uninstaller { get; }
    public PerformanceViewModel Performance { get; }
    public StartupViewModel Startup { get; }
    public NetworkSharedState NetworkShared { get; }
    public PingViewModel Ping { get; }
    public TracerouteViewModel Traceroute { get; }
    public SpeedTestViewModel SpeedTest { get; }
    public NetworkRepairViewModel NetworkRepair { get; }
    public DriversViewModel Drivers { get; }
    public LogsViewModel Logs { get; }
    public AboutViewModel About { get; }
    public ServicesViewModel Services { get; }

    // ── Implemented ViewModels (resolved from DI at runtime) ────────
    public WindowsFeaturesViewModel WindowsFeatures { get; }
    public AppAlertsViewModel AppAlerts { get; }
    public ShortcutCleanerViewModel ShortcutCleaner { get; }
    public AppBlockerViewModel AppBlocker { get; }
    public BulkInstallerViewModel BulkInstaller { get; }
    public FileShredderViewModel FileShredder { get; }
    public PrivacyViewModel Privacy { get; }
    public DnsHostsViewModel DnsHosts { get; }
    public ContextMenuViewModel ContextMenu { get; }
    public SystemReportViewModel SystemReport { get; }
    public EnvironmentVariablesViewModel EnvironmentVariables { get; }
    public RestorePointsViewModel RestorePoints { get; }
    public DebloaterViewModel Debloater { get; }
    public LegacyPanelsViewModel LegacyPanels { get; }
    public SystemFixesViewModel SystemFixes { get; }
    public ProfileViewModel Profile { get; }
    public BrowserCleanerViewModel BrowserCleaner { get; }
    public PrivacyMonitorViewModel PrivacyMonitor { get; }
    public BootAnalyzerViewModel BootAnalyzer { get; }
    public TimerResolutionViewModel TimerResolution { get; }
    public FileLockViewModel FileLock { get; }
    public DisplayProfileViewModel DisplayProfile { get; }
    public CpuAffinityViewModel CpuAffinity { get; }
    public DefenderViewModel Defender { get; }
    public TaskSchedulerViewModel TaskScheduler { get; }
    public DarkModeViewModel DarkMode { get; }
    public StandbyMemoryViewModel StandbyMemory { get; }

    // ── Placeholder ViewModels for planned features (WIP) ──────────
    // Monitor group
    public PlaceholderViewModel WipResourceHistory { get; private set; } = null!;
    public PlaceholderViewModel WipFileLockDetector { get; private set; } = null!;
    public PlaceholderViewModel WipSettingsWatchdog { get; private set; } = null!;
    public PlaceholderViewModel WipBandwidthMonitor { get; private set; } = null!;

    // Gaming & Profiles group
    public PlaceholderViewModel WipGamingProfile { get; private set; } = null!;
    public PlaceholderViewModel WipStandbyListCleaner { get; private set; } = null!;
    public PlaceholderViewModel WipTimerResolution { get; private set; } = null!;
    public PlaceholderViewModel WipCpuAffinity { get; private set; } = null!;
    public PlaceholderViewModel WipDisplayProfiles { get; private set; } = null!;

    // Cleanup group (File Shredder is now fully implemented)
    public PlaceholderViewModel WipScheduledMaintenance { get; private set; } = null!;

    // Network group — DNS Changer + Hosts Editor now fully implemented as DnsHosts

    // Apps group (Bulk Installer is now fully implemented)

    // Privacy & Security group (Privacy & Telemetry + Debloater + Browser Cleaner now fully implemented)
    public PlaceholderViewModel WipEdgeOneDriveRemover { get; private set; } = null!;
    public PlaceholderViewModel WipDefenderTweaks { get; private set; } = null!;
    public PlaceholderViewModel WipNotificationBlocker { get; private set; } = null!;

    // Customization group (Context Menu is now fully implemented)
    public PlaceholderViewModel WipDarkModeScheduler { get; private set; } = null!;
    public PlaceholderViewModel WipVolumeControl { get; private set; } = null!;

    // System group (additions)
    public PlaceholderViewModel WipTaskScheduler { get; private set; } = null!;

    // Advanced group
    public PlaceholderViewModel WipCliInterface { get; private set; } = null!;

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
    /// Parameterless constructor — used by XAML designer and tests.
    /// When DI container is available (runtime), resolves child VMs from it.
    /// When not available (tests/designer), creates dependencies manually.
    /// </summary>
    public MainWindowViewModel()
    {
        var sp = App.Services;
        if (sp is not null)
        {
            // Runtime path — resolve from DI container (shared singletons)
            Dashboard = sp.GetRequiredService<DashboardViewModel>();
            AppUpdates = sp.GetRequiredService<AppUpdatesViewModel>();
            WindowsUpdate = sp.GetRequiredService<WindowsUpdateViewModel>();
            SystemHealth = sp.GetRequiredService<SystemHealthViewModel>();
            Cleanup = sp.GetRequiredService<CleanupViewModel>();
            DeepCleanup = sp.GetRequiredService<DeepCleanupViewModel>();
            DuplicateFile = sp.GetRequiredService<DuplicateFileViewModel>();
            DiskAnalyzer = sp.GetRequiredService<DiskAnalyzerViewModel>();
            ProcessManager = sp.GetRequiredService<ProcessManagerViewModel>();
            BatteryHealth = sp.GetRequiredService<BatteryHealthViewModel>();
            Uninstaller = sp.GetRequiredService<UninstallerViewModel>();
            Performance = sp.GetRequiredService<PerformanceViewModel>();
            Startup = sp.GetRequiredService<StartupViewModel>();
            NetworkShared = sp.GetRequiredService<NetworkSharedState>();
            Ping = sp.GetRequiredService<PingViewModel>();
            Traceroute = sp.GetRequiredService<TracerouteViewModel>();
            SpeedTest = sp.GetRequiredService<SpeedTestViewModel>();
            NetworkRepair = sp.GetRequiredService<NetworkRepairViewModel>();
            Drivers = sp.GetRequiredService<DriversViewModel>();
            Logs = sp.GetRequiredService<LogsViewModel>();
            About = sp.GetRequiredService<AboutViewModel>();
            Services = sp.GetRequiredService<ServicesViewModel>();
            AppAlerts = sp.GetRequiredService<AppAlertsViewModel>();
            ShortcutCleaner = sp.GetRequiredService<ShortcutCleanerViewModel>();
            AppBlocker = sp.GetRequiredService<AppBlockerViewModel>();
            BulkInstaller = sp.GetRequiredService<BulkInstallerViewModel>();
            FileShredder = sp.GetRequiredService<FileShredderViewModel>();
            DnsHosts = sp.GetRequiredService<DnsHostsViewModel>();
            WindowsFeatures = sp.GetRequiredService<WindowsFeaturesViewModel>();
            Privacy = sp.GetRequiredService<PrivacyViewModel>();
            ContextMenu = sp.GetRequiredService<ContextMenuViewModel>();
            SystemReport = sp.GetRequiredService<SystemReportViewModel>();
            EnvironmentVariables = sp.GetRequiredService<EnvironmentVariablesViewModel>();
            RestorePoints = sp.GetRequiredService<RestorePointsViewModel>();
            Debloater = sp.GetRequiredService<DebloaterViewModel>();
            LegacyPanels = sp.GetRequiredService<LegacyPanelsViewModel>();
            SystemFixes = sp.GetRequiredService<SystemFixesViewModel>();
            Profile = sp.GetRequiredService<ProfileViewModel>();
            BrowserCleaner = sp.GetRequiredService<BrowserCleanerViewModel>();
            PrivacyMonitor = sp.GetRequiredService<PrivacyMonitorViewModel>();
            BootAnalyzer = sp.GetRequiredService<BootAnalyzerViewModel>();
            TimerResolution = sp.GetRequiredService<TimerResolutionViewModel>();
            FileLock = sp.GetRequiredService<FileLockViewModel>();
            DisplayProfile = sp.GetRequiredService<DisplayProfileViewModel>();
            CpuAffinity = sp.GetRequiredService<CpuAffinityViewModel>();
            Defender = sp.GetRequiredService<DefenderViewModel>();
            TaskScheduler = sp.GetRequiredService<TaskSchedulerViewModel>();
            DarkMode = sp.GetRequiredService<DarkModeViewModel>();
            StandbyMemory = sp.GetRequiredService<StandbyMemoryViewModel>();
        }
        else
        {
            // Test/designer path — create dependencies manually
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

            Dashboard = new DashboardViewModel(sysInfo, tuneUp, healthScore, new TemperatureService(diskHealth));
            AppUpdates = new AppUpdatesViewModel(winget);
            WindowsUpdate = new WindowsUpdateViewModel(runner, new WindowsUpdateService(), new WindowsUpdatePolicyService());
            SystemHealth = new SystemHealthViewModel(sysInfo, diskHealth, new MemoryTestService(), fixedDrives, runner, new BiosService());
            Cleanup = new CleanupViewModel(runner);
            DeepCleanup = new DeepCleanupViewModel(new DeepCleanupService(), new LargeFileScanner(), fixedDrives);
            DuplicateFile = new DuplicateFileViewModel(new DuplicateFileService());
            DiskAnalyzer = new DiskAnalyzerViewModel(new DiskAnalyzerService());
            ProcessManager = new ProcessManagerViewModel(new ProcessManagerService());
            BatteryHealth = new BatteryHealthViewModel(battery);
            Uninstaller = new UninstallerViewModel(new UninstallerService(runner));
            var restorePoints = new RestorePointService(runner);
            Performance = new PerformanceViewModel(new PerformanceService(runner, restorePoints));
            Startup = new StartupViewModel(new StartupService());
            NetworkShared = new NetworkSharedState(pinger, tracer, traceMonitor, speedTest, netRepair);
            Ping = new PingViewModel(NetworkShared);
            Traceroute = new TracerouteViewModel(NetworkShared);
            SpeedTest = new SpeedTestViewModel(NetworkShared, new SpeedTestHistoryService());
            NetworkRepair = new NetworkRepairViewModel(NetworkShared);
            Drivers = new DriversViewModel(runner);
            Logs = new LogsViewModel(new EventLogService());
            About = new AboutViewModel();
            Services = new ServicesViewModel(runner);
            AppAlerts = new AppAlertsViewModel(new AppAlertService());
            ShortcutCleaner = new ShortcutCleanerViewModel(shortcuts);
            AppBlocker = new AppBlockerViewModel(new AppBlockerService());
            BulkInstaller = new BulkInstallerViewModel(new BulkInstallerService(new PowerShellRunner()), new AppIconService());
            FileShredder = new FileShredderViewModel(new FileShredderService());
            DnsHosts = new DnsHostsViewModel(new DnsService(new PowerShellRunner()), new HostsFileService());
            WindowsFeatures = new WindowsFeaturesViewModel(new WindowsFeaturesService(runner));
            Privacy = new PrivacyViewModel(new PrivacyService());
            ContextMenu = new ContextMenuViewModel(new ContextMenuService());
            SystemReport = new SystemReportViewModel(new SystemReportService(sysInfo, diskHealth));
            EnvironmentVariables = new EnvironmentVariablesViewModel(new EnvironmentVariableService());
            RestorePoints = new RestorePointsViewModel(restorePoints);
            Debloater = new DebloaterViewModel(new DebloaterService(new PowerShellRunner()));
            LegacyPanels = new LegacyPanelsViewModel(new LegacyPanelService());
            SystemFixes = new SystemFixesViewModel(new SystemFixService(new PowerShellRunner()));
            Profile = new ProfileViewModel(new ProfileService());
            BrowserCleaner = new BrowserCleanerViewModel(new BrowserCleanerService());
            PrivacyMonitor = new PrivacyMonitorViewModel(new PrivacyMonitorService());
            BootAnalyzer = new BootAnalyzerViewModel(new BootAnalyzerService());
            TimerResolution = new TimerResolutionViewModel(new TimerResolutionService());
            FileLock = new FileLockViewModel(new FileLockService());
            DisplayProfile = new DisplayProfileViewModel(new DisplayProfileService());
            CpuAffinity = new CpuAffinityViewModel(new CpuAffinityService());
            Defender = new DefenderViewModel(new DefenderService(new PowerShellRunner()));
            TaskScheduler = new TaskSchedulerViewModel(new TaskSchedulerService(new PowerShellRunner()));
            DarkMode = new DarkModeViewModel(new WindowsThemeService());
            StandbyMemory = new StandbyMemoryViewModel(new StandbyMemoryService());
        }

        InitPlaceholders();
        InitNavigation();
    }

    private void InitPlaceholders()
    {
        // ── WIP placeholders for planned features ──────────────────────

        // Monitor group
        WipResourceHistory = new PlaceholderViewModel("Resource History", "Historical CPU, RAM, GPU and temperature graphs with timeline.", "#13");
        WipFileLockDetector = new PlaceholderViewModel("File Lock Detector", "Find which process is locking a file and optionally release the handle.", "#333");
        WipSettingsWatchdog = new PlaceholderViewModel("Settings Watchdog", "Detect when Windows Update resets your settings and offer one-click restore.", "#335");
        WipBandwidthMonitor = new PlaceholderViewModel("Bandwidth Monitor", "Real-time per-app network usage with history graphs and alerts.", "#337");

        // Gaming & Profiles group
        WipGamingProfile = new PlaceholderViewModel("Gaming Profile", "One-click game mode: kill background processes, clear RAM, set timer resolution, auto-revert on game exit.", "#324");
        WipStandbyListCleaner = new PlaceholderViewModel("Standby List Cleaner", "Automatic standby memory purging when free RAM drops below threshold (ISLC-style).", "#325");
        WipTimerResolution = new PlaceholderViewModel("Timer Resolution", "Set Windows timer to 0.5ms for reduced input lag in competitive games.", "#326");
        WipCpuAffinity = new PlaceholderViewModel("CPU Core Affinity", "Set per-game CPU affinity with P-core/E-core awareness for Intel hybrid CPUs.", "#327");
        WipDisplayProfiles = new PlaceholderViewModel("Display Profiles", "Quick-switch refresh rate, HDR, resolution presets (Gaming/Work/Movie).", "#328");

        // Cleanup group (File Shredder is now fully implemented)
        WipScheduledMaintenance = new PlaceholderViewModel("Scheduled Maintenance", "Automate cleanup, RAM trim, and health checks on schedule or idle trigger.", "#10");

        // Network group — DNS Changer + Hosts Editor now fully implemented as DnsHosts

        // Apps group — Bulk Installer is now fully implemented (no placeholder needed)

        // Privacy & Security group (Privacy & Telemetry is now fully implemented)
        WipEdgeOneDriveRemover = new PlaceholderViewModel("Edge/OneDrive Remover", "Safely remove or disable Edge and OneDrive with full restore capability.", "#339");
        WipDefenderTweaks = new PlaceholderViewModel("Defender Tweaks", "Toggle SmartScreen, manage exclusions, configure PUA and cloud protection.", "#344");
        WipNotificationBlocker = new PlaceholderViewModel("Notification Blocker", "Suppress annoying app pop-ups (update nags, trial reminders) with allowlist.", "#340");

        // Customization group (Context Menu is now fully implemented)
        WipDarkModeScheduler = new PlaceholderViewModel("Dark Mode Scheduler", "Auto light/dark theme + color temperature (f.lux-style) on schedule or sunset.", "#329");
        WipVolumeControl = new PlaceholderViewModel("Volume Control", "Per-app volume mixer with output device routing and profile presets.", "#332");

        // System group (additions)
        WipTaskScheduler = new PlaceholderViewModel("Task Scheduler", "Browse and toggle scheduled tasks with color-coded safety indicators.", "#334");

        // Advanced group
        WipCliInterface = new PlaceholderViewModel("CLI Interface", "Command-line control: sysmanager --cleanup --apply-profile Gaming --silent.", "#342");
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
                item.WireBusy();
                NavItems.Add(item);
            }
        }

        SelectedNav = NavItems[0];
    }

    private NavGroup[] BuildNavGroups() =>
    [
        Group("grp-dashboard", "Dashboard", "",
            Item("nav-dashboard", "Dashboard", "", Dashboard, typeof(Views.DashboardView))),

        Group("grp-system", "System", "",
            Item("nav-system-health",    "System Health",    "", SystemHealth,     typeof(Views.SystemHealthView)),
            Item("nav-windows-update",   "Windows Update",   "", WindowsUpdate,    typeof(Views.WindowsUpdateView)),
            Item("nav-performance",      "Performance Mode", "", Performance,      typeof(Views.PerformanceView)),
            Item("nav-services",         "Services",         "", Services,         typeof(Views.ServicesView)),
            Item("nav-startup",          "Startup Manager",  "", Startup,          typeof(Views.StartupView)),
            Item("nav-windows-features", "Windows Features", "", WindowsFeatures,  typeof(Views.WindowsFeaturesView)),
            Item("nav-restore-points",   "Restore Points",   "", RestorePoints,    typeof(Views.RestorePointsView)),
            Item("nav-task-scheduler",   "Task Scheduler",   "", TaskScheduler,    typeof(Views.TaskSchedulerView)),
            Item("nav-boot-analyzer",    "Boot Analyzer",    "", BootAnalyzer,     typeof(Views.BootAnalyzerView)),
            Item("nav-system-fixes",     "System Fixes",     "", SystemFixes,      typeof(Views.SystemFixesView))),

        Group("grp-gaming", "Gaming & Profiles", "",
            Item("nav-gaming-profile",   "Gaming Profile",       "", WipGamingProfile,      typeof(Views.PlaceholderView)),
            Item("nav-standby-cleaner",  "Standby List Cleaner", "", StandbyMemory,         typeof(Views.StandbyMemoryView)),
            Item("nav-timer-resolution", "Timer Resolution",     "", TimerResolution,       typeof(Views.TimerResolutionView)),
            Item("nav-cpu-affinity",     "CPU Core Affinity",    "", CpuAffinity,           typeof(Views.CpuAffinityView)),
            Item("nav-display-profiles", "Display Profiles",     "", DisplayProfile,        typeof(Views.DisplayProfileView))),

        Group("grp-monitor", "Monitor", "",
            Item("nav-processes",         "Process Manager",    "", ProcessManager,      typeof(Views.ProcessManagerView)),
            Item("nav-resource-history",  "Resource History",   "", WipResourceHistory,  typeof(Views.PlaceholderView)),
            Item("nav-privacy-monitor",   "Camera/Mic/Location",    "", PrivacyMonitor,      typeof(Views.PrivacyMonitorView)),
            Item("nav-app-alerts",        "App Alerts",         "", AppAlerts,           typeof(Views.AppAlertsView)),
            Item("nav-file-lock",         "File Lock Detector", "", FileLock,            typeof(Views.FileLockView)),
            Item("nav-settings-watchdog", "Settings Watchdog",  "", WipSettingsWatchdog, typeof(Views.PlaceholderView)),
            Item("nav-bandwidth-monitor", "Bandwidth Monitor",  "", WipBandwidthMonitor, typeof(Views.PlaceholderView))),

        Group("grp-cleanup", "Cleanup", "",
            Item("nav-cleanup",               "Quick Cleanup",         "", Cleanup,                 typeof(Views.CleanupView)),
            Item("nav-deep-cleanup",          "Deep Cleanup",          "", DeepCleanup,             typeof(Views.DeepCleanupView)),
            Item("nav-shortcut-cleaner",      "Shortcut Cleaner",      "", ShortcutCleaner,         typeof(Views.ShortcutCleanerView)),
            Item("nav-scheduled-maintenance", "Scheduled Maintenance", "", WipScheduledMaintenance, typeof(Views.PlaceholderView))),

        Group("grp-storage", "Storage", "",
            Item("nav-disk-analyzer", "Disk Analyzer",    "", DiskAnalyzer,  typeof(Views.DiskAnalyzerView)),
            Item("nav-duplicates",    "Duplicate Finder", "", DuplicateFile, typeof(Views.DuplicateFileView))),

        Group("grp-network", "Network", "",
            Item("nav-ping",           "Ping",           "", Ping,          typeof(Views.PingView)),
            Item("nav-traceroute",     "Traceroute",     "", Traceroute,    typeof(Views.TracerouteView)),
            Item("nav-speed-test",     "Speed Test",     "", SpeedTest,     typeof(Views.SpeedTestView)),
            Item("nav-network-repair", "Network Repair", "", NetworkRepair, typeof(Views.NetworkRepairView)),
            Item("nav-dns-hosts",      "DNS & Hosts",    "", DnsHosts,      typeof(Views.DnsHostsView))),

        Group("grp-apps", "Apps", "",
            Item("nav-app-updates",    "App Updates",    "", AppUpdates,    typeof(Views.AppUpdatesView)),
            Item("nav-bulk-installer", "Bulk Installer", "", BulkInstaller, typeof(Views.BulkInstallerView)),
            Item("nav-uninstaller",    "Uninstaller",    "", Uninstaller,   typeof(Views.UninstallerView))),

        Group("grp-privacy", "Privacy & Security", "",
            Item("nav-privacy-settings",     "Privacy & Telemetry",   "", Privacy,                typeof(Views.PrivacyView)),
            Item("nav-file-shredder",        "File Shredder",         "", FileShredder,           typeof(Views.FileShredderView)),
            Item("nav-app-blocker",          "App Blocker",           "", AppBlocker,             typeof(Views.AppBlockerView)),
            Item("nav-debloater",            "Debloater & Ads",       "", Debloater,             typeof(Views.DebloaterView)),
            Item("nav-browser-cleaner",      "Browser Cleaner",       "", BrowserCleaner,         typeof(Views.BrowserCleanerView)),
            Item("nav-edge-onedrive",        "Edge/OneDrive Remover", "", WipEdgeOneDriveRemover, typeof(Views.PlaceholderView)),
            Item("nav-defender-tweaks",      "Defender Tweaks",       "", Defender,               typeof(Views.DefenderView)),
            Item("nav-notification-blocker", "Notification Blocker",  "", WipNotificationBlocker, typeof(Views.PlaceholderView))),

        Group("grp-customization", "Customization", "",
            Item("nav-context-menu",   "Context Menu",          "", ContextMenu,          typeof(Views.ContextMenuView)),
            Item("nav-dark-mode",      "Dark Mode Scheduler",   "", DarkMode,             typeof(Views.DarkModeView)),
            Item("nav-volume-control", "Volume Control",        "", WipVolumeControl,     typeof(Views.PlaceholderView))),

        Group("grp-info", "Info", "",
            Item("nav-drivers",       "Drivers",        "", Drivers,        typeof(Views.DriversView)),
            Item("nav-battery",       "Battery Health", "", BatteryHealth,  typeof(Views.BatteryHealthView)),
            Item("nav-logs",          "System Logs",    "", Logs,           typeof(Views.LogsView)),
            Item("nav-system-report", "System Report",  "", SystemReport, typeof(Views.SystemReportView)),
            Item("nav-legacy-panels", "Legacy Panels",  "", LegacyPanels, typeof(Views.LegacyPanelsView)),
            Item("nav-about",         "About",          "", About,          typeof(Views.AboutView))),

        Group("grp-advanced", "Advanced", "",
            Item("nav-profile-export", "Profile Export/Import", "", Profile,               typeof(Views.ProfileView)),
            Item("nav-cli-interface",  "CLI Interface",         "", WipCliInterface,        typeof(Views.PlaceholderView)),
            Item("nav-env-variables",  "Environment Variables", "", EnvironmentVariables, typeof(Views.EnvironmentVariablesView))),
    ];

    private static NavGroup Group(string id, string label, string glyph, params NavItem[] children)
    {
        var g = new NavGroup { Id = id, Label = label, Glyph = glyph };
        foreach (var c in children) g.Children.Add(c);
        g.Subtitle = string.Join(" · ", children.Select(c => c.Label));
        g.Tooltip = string.Join("\n", children.Select(c => c.Label));
        return g;
    }

    private static NavItem Item(string id, string label, string glyph, object content, Type viewType, bool inDevelopment = false)
        => new() { Id = id, Label = label, Glyph = glyph, Content = content, ViewType = viewType, IsInDevelopment = inDevelopment };

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;
        Log.Information("Tab navigated: {TabLabel}", value.Label);

        // Auto-expand the parent group when a child is selected.
        var parentGroup = NavGroups.FirstOrDefault(g => g.Children.Contains(value));
        if (parentGroup is not null) parentGroup.IsExpanded = true;

        // Pause/resume the process manager auto-refresh loop based on tab visibility.
        ProcessManager.IsActive = ReferenceEquals(value.Content, ProcessManager);
        Dashboard.IsActive = ReferenceEquals(value.Content, Dashboard);
    }

    /// <summary>Select a nav item by its automation id.</summary>
    private void SelectNavById(string id)
    {
        var item = NavItems.FirstOrDefault(n => n.Id == id);
        if (item is not null) SelectedNav = item;
    }

    [RelayCommand]
    private void OpenAboutTab() => SelectNavById("nav-about");

    [RelayCommand]
    private void OpenDeepCleanupTab() => SelectNavById("nav-deep-cleanup");

    [RelayCommand]
    private void OpenDiskAnalyzerTab() => SelectNavById("nav-disk-analyzer");

    [RelayCommand]
    private void OpenDuplicatesTab() => SelectNavById("nav-duplicates");

    [RelayCommand]
    private void OpenCleanupTab() => SelectNavById("nav-cleanup");

    [RelayCommand]
    private void OpenSystemHealthTab() => SelectNavById("nav-system-health");

    public void Dispose()
    {
        // Idempotency guard: Dispose is wired to two shutdown paths (OnClosed and
        // Application.Exit), so it can run more than once. Disposing the SkiaSharp
        // paint handles (via NetworkShared) twice is undefined behavior.
        if (_disposed) return;
        _disposed = true;

        // Dispose NavItems to unsubscribe PropertyChanged handlers
        foreach (var item in NavItems)
            item.Dispose();

        Dashboard?.Dispose();
        AppUpdates?.Dispose();
        WindowsUpdate?.Dispose();
        SystemHealth?.Dispose();
        Cleanup?.Dispose();
        DeepCleanup?.Dispose();
        DuplicateFile?.Dispose();
        DiskAnalyzer?.Dispose();
        ProcessManager?.Dispose();
        BatteryHealth?.Dispose();
        Uninstaller?.Dispose();
        Performance?.Dispose();
        Startup?.Dispose();
        Ping?.Dispose();
        Traceroute?.Dispose();
        SpeedTest?.Dispose();
        NetworkRepair?.Dispose();
        Drivers?.Dispose();
        Logs?.Dispose();
        About?.Dispose();
        Services?.Dispose();
        NetworkShared?.Dispose();
        WindowsFeatures?.Dispose();
        AppAlerts?.Dispose();
        ShortcutCleaner?.Dispose();
        AppBlocker?.Dispose();
        BulkInstaller?.Dispose();
        FileShredder?.Dispose();
        DnsHosts?.Dispose();

        // Implemented PREVIEW tabs — these own timers / event subscriptions
        // (e.g. DisplayProfile's revert DispatcherTimer, Defender's PropertyChanged),
        // so they must be disposed on shutdown like every other real VM.
        TimerResolution?.Dispose();
        FileLock?.Dispose();
        DisplayProfile?.Dispose();
        CpuAffinity?.Dispose();
        Defender?.Dispose();
        TaskScheduler?.Dispose();
        DarkMode?.Dispose();
        StandbyMemory?.Dispose();

        // WIP placeholders
        WipResourceHistory?.Dispose();
        WipFileLockDetector?.Dispose();
        WipSettingsWatchdog?.Dispose();
        WipBandwidthMonitor?.Dispose();
        WipGamingProfile?.Dispose();
        WipStandbyListCleaner?.Dispose();
        WipTimerResolution?.Dispose();
        WipCpuAffinity?.Dispose();
        WipDisplayProfiles?.Dispose();
        WipScheduledMaintenance?.Dispose();
        Privacy?.Dispose();
        ContextMenu?.Dispose();
        SystemReport?.Dispose();
        EnvironmentVariables?.Dispose();
        RestorePoints?.Dispose();
        Debloater?.Dispose();
        LegacyPanels?.Dispose();
        SystemFixes?.Dispose();
        Profile?.Dispose();
        BrowserCleaner?.Dispose();
        PrivacyMonitor?.Dispose();
        BootAnalyzer?.Dispose();
        WipEdgeOneDriveRemover?.Dispose();
        WipDefenderTweaks?.Dispose();
        WipNotificationBlocker?.Dispose();
        WipDarkModeScheduler?.Dispose();
        WipVolumeControl?.Dispose();
        WipTaskScheduler?.Dispose();
        WipCliInterface?.Dispose();

        GC.SuppressFinalize(this);
    }
}
