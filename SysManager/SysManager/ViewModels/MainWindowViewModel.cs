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

    // ── Placeholder ViewModels for planned features (WIP) ──────────
    // Monitor group
    public PlaceholderViewModel WipResourceHistory { get; private set; } = null!;
    public PlaceholderViewModel WipPrivacyMonitor { get; private set; } = null!;
    public PlaceholderViewModel WipFileLockDetector { get; private set; } = null!;
    public PlaceholderViewModel WipSettingsWatchdog { get; private set; } = null!;
    public PlaceholderViewModel WipBandwidthMonitor { get; private set; } = null!;

    // Gaming & Profiles group
    public PlaceholderViewModel WipGamingProfile { get; private set; } = null!;
    public PlaceholderViewModel WipStandbyListCleaner { get; private set; } = null!;
    public PlaceholderViewModel WipTimerResolution { get; private set; } = null!;
    public PlaceholderViewModel WipCpuAffinity { get; private set; } = null!;
    public PlaceholderViewModel WipDisplayProfiles { get; private set; } = null!;

    // Cleanup group
    public PlaceholderViewModel WipFileShredder { get; private set; } = null!;
    public PlaceholderViewModel WipScheduledMaintenance { get; private set; } = null!;

    // Network group
    public PlaceholderViewModel WipDnsChanger { get; private set; } = null!;
    public PlaceholderViewModel WipHostsEditor { get; private set; } = null!;

    // Apps group (Bulk Installer is now fully implemented)

    // Privacy & Security group
    public PlaceholderViewModel WipPrivacySettings { get; private set; } = null!;
    public PlaceholderViewModel WipDebloater { get; private set; } = null!;
    public PlaceholderViewModel WipBrowserCleaner { get; private set; } = null!;
    public PlaceholderViewModel WipEdgeOneDriveRemover { get; private set; } = null!;
    public PlaceholderViewModel WipDefenderTweaks { get; private set; } = null!;
    public PlaceholderViewModel WipNotificationBlocker { get; private set; } = null!;

    // Customization group
    public PlaceholderViewModel WipContextMenu { get; private set; } = null!;
    public PlaceholderViewModel WipDarkModeScheduler { get; private set; } = null!;
    public PlaceholderViewModel WipVolumeControl { get; private set; } = null!;
    public PlaceholderViewModel WipEnvVariableEditor { get; private set; } = null!;

    // System group (additions)
    public PlaceholderViewModel WipTaskScheduler { get; private set; } = null!;
    public PlaceholderViewModel WipBootAnalyzer { get; private set; } = null!;

    // Advanced group
    public PlaceholderViewModel WipRestorePoints { get; private set; } = null!;
    public PlaceholderViewModel WipProfileExportImport { get; private set; } = null!;
    public PlaceholderViewModel WipCliInterface { get; private set; } = null!;
    public PlaceholderViewModel WipSystemReport { get; private set; } = null!;

    /// <summary>Grouped sidebar tree (12 categories).</summary>
    public ObservableCollection<NavGroup> NavGroups { get; } = new();

    /// <summary>Flat list of every leaf NavItem (backward compat + lookup).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private string _title = "SysManager";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _elevationBadge = "";

    /// <summary>
    /// Parameterless constructor — used by XAML designer and tests.
    /// When DI container is available (runtime), resolves child VMs from it.
    /// When not available (tests/designer), creates dependencies manually.
    /// </summary>
    public MainWindowViewModel()
    {
        var sp = App.Services;
        if (sp != null)
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
            WindowsFeatures = sp.GetRequiredService<WindowsFeaturesViewModel>();
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

            Dashboard = new DashboardViewModel(sysInfo, tuneUp, healthScore);
            AppUpdates = new AppUpdatesViewModel(winget);
            WindowsUpdate = new WindowsUpdateViewModel(runner);
            SystemHealth = new SystemHealthViewModel(sysInfo, diskHealth, new MemoryTestService(), fixedDrives, runner);
            Cleanup = new CleanupViewModel(runner);
            DeepCleanup = new DeepCleanupViewModel(new DeepCleanupService(), new LargeFileScanner(), fixedDrives);
            DuplicateFile = new DuplicateFileViewModel(new DuplicateFileService());
            DiskAnalyzer = new DiskAnalyzerViewModel(new DiskAnalyzerService());
            ProcessManager = new ProcessManagerViewModel(new ProcessManagerService());
            BatteryHealth = new BatteryHealthViewModel(battery);
            Uninstaller = new UninstallerViewModel(new UninstallerService(runner));
            Performance = new PerformanceViewModel(new PerformanceService(runner));
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
            AppBlocker = new AppBlockerViewModel();
            BulkInstaller = new BulkInstallerViewModel(new BulkInstallerService(new PowerShellRunner()));
            WindowsFeatures = new WindowsFeaturesViewModel(new WindowsFeaturesService(runner));
        }

        InitPlaceholders();
        InitNavigation();
    }

    private void InitPlaceholders()
    {
        // ── WIP placeholders for planned features ──────────────────────

        // Monitor group
        WipResourceHistory = new PlaceholderViewModel("Resource History", "Historical CPU, RAM, GPU and temperature graphs with timeline.", "#13");
        WipPrivacyMonitor = new PlaceholderViewModel("Privacy Monitor", "Monitor and alert on webcam, microphone, and location access in real-time.", "#12");
        WipFileLockDetector = new PlaceholderViewModel("File Lock Detector", "Find which process is locking a file and optionally release the handle.", "#333");
        WipSettingsWatchdog = new PlaceholderViewModel("Settings Watchdog", "Detect when Windows Update resets your settings and offer one-click restore.", "#335");
        WipBandwidthMonitor = new PlaceholderViewModel("Bandwidth Monitor", "Real-time per-app network usage with history graphs and alerts.", "#337");

        // Gaming & Profiles group
        WipGamingProfile = new PlaceholderViewModel("Gaming Profile", "One-click game mode: kill background processes, clear RAM, set timer resolution, auto-revert on game exit.", "#324");
        WipStandbyListCleaner = new PlaceholderViewModel("Standby List Cleaner", "Automatic standby memory purging when free RAM drops below threshold (ISLC-style).", "#325");
        WipTimerResolution = new PlaceholderViewModel("Timer Resolution", "Set Windows timer to 0.5ms for reduced input lag in competitive games.", "#326");
        WipCpuAffinity = new PlaceholderViewModel("CPU Core Affinity", "Set per-game CPU affinity with P-core/E-core awareness for Intel hybrid CPUs.", "#327");
        WipDisplayProfiles = new PlaceholderViewModel("Display Profiles", "Quick-switch refresh rate, HDR, resolution presets (Gaming/Work/Movie).", "#328");

        // Cleanup group
        WipFileShredder = new PlaceholderViewModel("File Shredder", "Securely delete files beyond recovery with multi-pass overwrite (DoD/Gutmann).", "#7");
        WipScheduledMaintenance = new PlaceholderViewModel("Scheduled Maintenance", "Automate cleanup, RAM trim, and health checks on schedule or idle trigger.", "#10");

        // Network group
        WipDnsChanger = new PlaceholderViewModel("DNS Changer", "Quick-switch DNS with benchmark, DNS-over-HTTPS, and TCP/IP optimization.", "#11");
        WipHostsEditor = new PlaceholderViewModel("Hosts Editor", "GUI hosts file editor with domain toggle, import block lists, and backup/restore.", "#11");

        // Apps group — Bulk Installer is now fully implemented (no placeholder needed)

        // Privacy & Security group
        WipPrivacySettings = new PlaceholderViewModel("Privacy & Telemetry", "80-100+ toggles for telemetry, ads, AI/Copilot, location, diagnostics with presets.", "#9");
        WipDebloater = new PlaceholderViewModel("Debloater & Ads", "Remove UWP bloatware, disable all Windows ads, remove Copilot/Recall/AI features.", "#9");
        WipBrowserCleaner = new PlaceholderViewModel("Browser Cleaner", "Per-browser cache/cookies/history cleanup with keep-list for important cookies.", "#336");
        WipEdgeOneDriveRemover = new PlaceholderViewModel("Edge/OneDrive Remover", "Safely remove or disable Edge and OneDrive with full restore capability.", "#339");
        WipDefenderTweaks = new PlaceholderViewModel("Defender Tweaks", "Toggle SmartScreen, manage exclusions, configure PUA and cloud protection.", "#344");
        WipNotificationBlocker = new PlaceholderViewModel("Notification Blocker", "Suppress annoying app pop-ups (update nags, trial reminders) with allowlist.", "#340");

        // Customization group
        WipContextMenu = new PlaceholderViewModel("Context Menu", "Manage right-click entries, restore Win10 full menu, add custom items.", "#8");
        WipDarkModeScheduler = new PlaceholderViewModel("Dark Mode Scheduler", "Auto light/dark theme + color temperature (f.lux-style) on schedule or sunset.", "#329");
        WipVolumeControl = new PlaceholderViewModel("Volume Control", "Per-app volume mixer with output device routing and profile presets.", "#332");
        WipEnvVariableEditor = new PlaceholderViewModel("Environment Variables", "GUI PATH editor with drag-reorder, duplicate detection, and path validation.", "#331");

        // System group (additions)
        WipTaskScheduler = new PlaceholderViewModel("Task Scheduler", "Browse and toggle scheduled tasks with color-coded safety indicators.", "#334");
        WipBootAnalyzer = new PlaceholderViewModel("Boot Analyzer", "Measure boot time breakdown per service/driver with optimization recommendations.", "#343");

        // Advanced group
        WipRestorePoints = new PlaceholderViewModel("Restore Points", "Create and manage system restore points with size tracking.", "#10");
        WipProfileExportImport = new PlaceholderViewModel("Profile Export/Import", "Export SysManager configuration as JSON, import on another PC.", "#341");
        WipCliInterface = new PlaceholderViewModel("CLI Interface", "Command-line control: sysmanager --cleanup --apply-profile Gaming --silent.", "#342");
        WipSystemReport = new PlaceholderViewModel("System Report", "Comprehensive system info export (better than DxDiag) in HTML/JSON.", "#4");
    }

    private void InitNavigation()
    {
        IsElevated = AdminHelper.IsElevated();
        ElevationBadge = IsElevated ? "Administrator" : "Standard user";
        Title = IsElevated ? "SysManager — Administrator" : "SysManager";
        Log.Information("MainWindow initialized. Elevated: {IsElevated}", IsElevated);

        // ── Sidebar tree: 12 groups ────────────────────────────────────
        // Views are instantiated lazily on first access — lets unit tests
        // construct the VM on an MTA thread without pulling WPF resources in.

        // 🏠 Dashboard (single-item group — renders flat)
        var grpDashboard = new NavGroup
        {
            Id = "grp-dashboard",
            Label = "Dashboard",
            Glyph = "\uE80F",
            Children = {
            new NavItem { Id = "nav-dashboard", Label = "Dashboard", Glyph = "\uE80F", Content = Dashboard, ViewType = typeof(Views.DashboardView) },
        }
        };

        // 🔧 System (8)
        var grpSystem = new NavGroup
        {
            Id = "grp-system",
            Label = "System",
            Glyph = "\uE912",
            Subtitle = "Health · WinUpdate · Perf · Services · Startup · Features · Tasks · Boot",
            Tooltip = "System Health\nWindows Update\nPerformance Mode\nServices\nStartup Manager\nWindows Features\nTask Scheduler\nBoot Analyzer",
            Children = {
            new NavItem { Id = "nav-system-health",     Label = "System Health",     Glyph = "\uE9D9", Content = SystemHealth,     ViewType = typeof(Views.SystemHealthView) },
            new NavItem { Id = "nav-windows-update",    Label = "Windows Update",    Glyph = "\uE895", Content = WindowsUpdate,    ViewType = typeof(Views.WindowsUpdateView) },
            new NavItem { Id = "nav-performance",       Label = "Performance Mode",  Glyph = "\uE945", Content = Performance,      ViewType = typeof(Views.PerformanceView) },
            new NavItem { Id = "nav-services",          Label = "Services",          Glyph = "\uE912", Content = Services,         ViewType = typeof(Views.ServicesView) },
            new NavItem { Id = "nav-startup",           Label = "Startup Manager",   Glyph = "\uE7B5", Content = Startup,          ViewType = typeof(Views.StartupView) },
            new NavItem { Id = "nav-windows-features",  Label = "Windows Features",  Glyph = "\uE9CE", Content = WindowsFeatures,  ViewType = typeof(Views.WindowsFeaturesView) },
            new NavItem { Id = "nav-task-scheduler",    Label = "Task Scheduler",    Glyph = "\uE916", Content = WipTaskScheduler, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-boot-analyzer",     Label = "Boot Analyzer",     Glyph = "\uE7F8", Content = WipBootAnalyzer,  ViewType = typeof(Views.PlaceholderView) },
        }
        };

        // 🎮 Gaming & Profiles (5) — NEW GROUP
        var grpGaming = new NavGroup
        {
            Id = "grp-gaming",
            Label = "Gaming & Profiles",
            Glyph = "\uE7FC",
            Subtitle = "Game Mode · Standby · Timer · Affinity · Display",
            Tooltip = "Gaming Profile\nStandby List Cleaner\nTimer Resolution\nCPU Core Affinity\nDisplay Profiles",
            Children = {
            new NavItem { Id = "nav-gaming-profile",   Label = "Gaming Profile",       Glyph = "\uE7FC", Content = WipGamingProfile,      ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-standby-cleaner",  Label = "Standby List Cleaner", Glyph = "\uE945", Content = WipStandbyListCleaner, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-timer-resolution", Label = "Timer Resolution",     Glyph = "\uE916", Content = WipTimerResolution,    ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-cpu-affinity",     Label = "CPU Core Affinity",    Glyph = "\uE950", Content = WipCpuAffinity,        ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-display-profiles", Label = "Display Profiles",     Glyph = "\uE7F4", Content = WipDisplayProfiles,    ViewType = typeof(Views.PlaceholderView) },
        }
        };

        // 📊 Monitor (7)
        var grpMonitor = new NavGroup
        {
            Id = "grp-monitor",
            Label = "Monitor",
            Glyph = "\uE9D9",
            Subtitle = "Processes · Resources · Alerts · Privacy · Lock · Watchdog · Bandwidth",
            Tooltip = "Process Manager\nResource History\nApp Alerts\nPrivacy Monitor\nFile Lock Detector\nSettings Watchdog\nBandwidth Monitor",
            Children = {
            new NavItem { Id = "nav-processes",         Label = "Process Manager",    Glyph = "\uEBC4", Content = ProcessManager,      ViewType = typeof(Views.ProcessManagerView) },
            new NavItem { Id = "nav-resource-history",  Label = "Resource History",   Glyph = "\uE9D9", Content = WipResourceHistory,  ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-app-alerts",        Label = "App Alerts",         Glyph = "\uEA8F", Content = AppAlerts,           ViewType = typeof(Views.AppAlertsView) },
            new NavItem { Id = "nav-privacy-monitor",   Label = "Privacy Monitor",    Glyph = "\uE727", Content = WipPrivacyMonitor,   ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-file-lock",         Label = "File Lock Detector", Glyph = "\uE72E", Content = WipFileLockDetector, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-settings-watchdog", Label = "Settings Watchdog",  Glyph = "\uE7BA", Content = WipSettingsWatchdog, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-bandwidth-monitor", Label = "Bandwidth Monitor",  Glyph = "\uE839", Content = WipBandwidthMonitor, ViewType = typeof(Views.PlaceholderView) },
        }
        };

        // 🧹 Cleanup (5)
        var grpCleanup = new NavGroup
        {
            Id = "grp-cleanup",
            Label = "Cleanup",
            Glyph = "\uE74D",
            Subtitle = "Quick · Deep · Shortcuts · Shredder · Maintenance",
            Tooltip = "Quick Cleanup\nDeep Cleanup\nShortcut Cleaner\nFile Shredder\nScheduled Maintenance",
            Children = {
            new NavItem { Id = "nav-cleanup",               Label = "Quick Cleanup",         Glyph = "\uE74D", Content = Cleanup,                 ViewType = typeof(Views.CleanupView) },
            new NavItem { Id = "nav-deep-cleanup",          Label = "Deep Cleanup",          Glyph = "\uE81E", Content = DeepCleanup,             ViewType = typeof(Views.DeepCleanupView) },
            new NavItem { Id = "nav-shortcut-cleaner",      Label = "Shortcut Cleaner",      Glyph = "\uE71B", Content = ShortcutCleaner,         ViewType = typeof(Views.ShortcutCleanerView) },
            new NavItem { Id = "nav-file-shredder",         Label = "File Shredder",         Glyph = "\uE74D", Content = WipFileShredder,         ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-scheduled-maintenance", Label = "Scheduled Maintenance", Glyph = "\uE823", Content = WipScheduledMaintenance, ViewType = typeof(Views.PlaceholderView) },
        }
        };

        // 💾 Storage (2)
        var grpStorage = new NavGroup
        {
            Id = "grp-storage",
            Label = "Storage",
            Glyph = "\uE958",
            Subtitle = "Disk Analyzer · Duplicate Finder",
            Tooltip = "Disk Analyzer\nDuplicate Finder",
            Children = {
            new NavItem { Id = "nav-disk-analyzer", Label = "Disk Analyzer",    Glyph = "\uE958", Content = DiskAnalyzer,  ViewType = typeof(Views.DiskAnalyzerView) },
            new NavItem { Id = "nav-duplicates",    Label = "Duplicate Finder", Glyph = "\uE8C8", Content = DuplicateFile, ViewType = typeof(Views.DuplicateFileView) },
        }
        };

        // 🌐 Network (6)
        var grpNetwork = new NavGroup
        {
            Id = "grp-network",
            Label = "Network",
            Glyph = "\uE839",
            Subtitle = "Ping · Traceroute · Speed · Repair · DNS · Hosts",
            Tooltip = "Ping\nTraceroute\nSpeed Test\nNetwork Repair\nDNS Changer\nHosts Editor",
            Children = {
            new NavItem { Id = "nav-ping",           Label = "Ping",           Glyph = "\uE839", Content = Ping,           ViewType = typeof(Views.PingView) },
            new NavItem { Id = "nav-traceroute",     Label = "Traceroute",     Glyph = "\uE8B0", Content = Traceroute,     ViewType = typeof(Views.TracerouteView) },
            new NavItem { Id = "nav-speed-test",     Label = "Speed Test",     Glyph = "\uE916", Content = SpeedTest,      ViewType = typeof(Views.SpeedTestView) },
            new NavItem { Id = "nav-network-repair", Label = "Network Repair", Glyph = "\uE90F", Content = NetworkRepair,  ViewType = typeof(Views.NetworkRepairView) },
            new NavItem { Id = "nav-dns-changer",    Label = "DNS Changer",    Glyph = "\uE968", Content = WipDnsChanger,  ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-hosts-editor",   Label = "Hosts Editor",   Glyph = "\uE8A5", Content = WipHostsEditor, ViewType = typeof(Views.PlaceholderView) },
        }
        };

        // 📦 Apps (4)
        var grpApps = new NavGroup
        {
            Id = "grp-apps",
            Label = "Apps",
            Glyph = "\uE7B8",
            Subtitle = "Updates · Installer · Uninstaller · Blocker",
            Tooltip = "App Updates\nBulk Installer\nUninstaller\nApp Blocker",
            Children = {
            new NavItem { Id = "nav-app-updates",    Label = "App Updates",    Glyph = "\uE7B8", Content = AppUpdates,       ViewType = typeof(Views.AppUpdatesView) },
            new NavItem { Id = "nav-bulk-installer", Label = "Bulk Installer", Glyph = "\uE896", Content = BulkInstaller, ViewType = typeof(Views.BulkInstallerView) },
            new NavItem { Id = "nav-uninstaller",    Label = "Uninstaller",    Glyph = "\uE738", Content = Uninstaller,      ViewType = typeof(Views.UninstallerView) },
            new NavItem { Id = "nav-app-blocker",    Label = "App Blocker",    Glyph = "\uE8F8", Content = AppBlocker,       ViewType = typeof(Views.AppBlockerView) },
        }
        };

        // 🛡️ Privacy & Security (6) — NEW GROUP
        var grpPrivacy = new NavGroup
        {
            Id = "grp-privacy",
            Label = "Privacy & Security",
            Glyph = "\uE72E",
            Subtitle = "Telemetry · Debloat · Browser · Edge/OneDrive · Defender · Notifications",
            Tooltip = "Privacy & Telemetry\nDebloater & Ads\nBrowser Cleaner\nEdge/OneDrive Remover\nDefender Tweaks\nNotification Blocker",
            Children = {
            new NavItem { Id = "nav-privacy-settings",     Label = "Privacy & Telemetry",   Glyph = "\uE72E", Content = WipPrivacySettings,     ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-debloater",            Label = "Debloater & Ads",       Glyph = "\uE74D", Content = WipDebloater,           ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-browser-cleaner",      Label = "Browser Cleaner",       Glyph = "\uEB41", Content = WipBrowserCleaner,      ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-edge-onedrive",        Label = "Edge/OneDrive Remover", Glyph = "\uE738", Content = WipEdgeOneDriveRemover, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-defender-tweaks",      Label = "Defender Tweaks",       Glyph = "\uE83D", Content = WipDefenderTweaks,      ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-notification-blocker", Label = "Notification Blocker",  Glyph = "\uE7ED", Content = WipNotificationBlocker, ViewType = typeof(Views.PlaceholderView) },
        }
        };

        // 🎨 Customization (4) — NEW GROUP
        var grpCustomization = new NavGroup
        {
            Id = "grp-customization",
            Label = "Customization",
            Glyph = "\uE771",
            Subtitle = "Context Menu · Dark Mode · Volume · Env Variables",
            Tooltip = "Context Menu\nDark Mode Scheduler\nVolume Control\nEnvironment Variables",
            Children = {
            new NavItem { Id = "nav-context-menu",   Label = "Context Menu",          Glyph = "\uE700", Content = WipContextMenu,       ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-dark-mode",      Label = "Dark Mode Scheduler",   Glyph = "\uE793", Content = WipDarkModeScheduler, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-volume-control", Label = "Volume Control",        Glyph = "\uE767", Content = WipVolumeControl,     ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-env-variables",  Label = "Environment Variables", Glyph = "\uE943", Content = WipEnvVariableEditor, ViewType = typeof(Views.PlaceholderView) },
        }
        };

        // ℹ️ Info (4)
        var grpInfo = new NavGroup
        {
            Id = "grp-info",
            Label = "Info",
            Glyph = "\uE946",
            Subtitle = "Drivers · Battery · Logs · About",
            Tooltip = "Drivers\nBattery Health\nSystem Logs\nAbout",
            Children = {
            new NavItem { Id = "nav-drivers", Label = "Drivers",        Glyph = "\uE950", Content = Drivers,       ViewType = typeof(Views.DriversView) },
            new NavItem { Id = "nav-battery", Label = "Battery Health", Glyph = "\uEBA6", Content = BatteryHealth, ViewType = typeof(Views.BatteryHealthView) },
            new NavItem { Id = "nav-logs",    Label = "System Logs",    Glyph = "\uE9F9", Content = Logs,          ViewType = typeof(Views.LogsView) },
            new NavItem { Id = "nav-about",   Label = "About",          Glyph = "\uE946", Content = About,         ViewType = typeof(Views.AboutView) },
        }
        };

        // ⚙️ Advanced (4) — NEW GROUP
        var grpAdvanced = new NavGroup
        {
            Id = "grp-advanced",
            Label = "Advanced",
            Glyph = "\uE713",
            Subtitle = "Restore · Export/Import · CLI · Report",
            Tooltip = "Restore Points\nProfile Export/Import\nCLI Interface\nSystem Report",
            Children = {
            new NavItem { Id = "nav-restore-points", Label = "Restore Points",       Glyph = "\uE7AD", Content = WipRestorePoints,       ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-profile-export", Label = "Profile Export/Import", Glyph = "\uE8B5", Content = WipProfileExportImport, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-cli-interface",  Label = "CLI Interface",         Glyph = "\uE756", Content = WipCliInterface,        ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-system-report",  Label = "System Report",         Glyph = "\uE9F9", Content = WipSystemReport,        ViewType = typeof(Views.PlaceholderView) },
        }
        };

        NavGroups.Add(grpDashboard);
        NavGroups.Add(grpSystem);
        NavGroups.Add(grpGaming);
        NavGroups.Add(grpMonitor);
        NavGroups.Add(grpCleanup);
        NavGroups.Add(grpStorage);
        NavGroups.Add(grpNetwork);
        NavGroups.Add(grpApps);
        NavGroups.Add(grpPrivacy);
        NavGroups.Add(grpCustomization);
        NavGroups.Add(grpInfo);
        NavGroups.Add(grpAdvanced);

        // Flat index for backward compat (Open*Tab commands, tests, automation).
        foreach (var g in NavGroups)
            foreach (var item in g.Children)
            {
                item.WireBusy();
                NavItems.Add(item);
            }

        SelectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        Log.Information("Tab navigated: {TabLabel}", value.Label);

        // Auto-expand the parent group when a child is selected.
        var parentGroup = NavGroups.FirstOrDefault(g => g.Children.Contains(value));
        if (parentGroup != null) parentGroup.IsExpanded = true;
    }

    /// <summary>Select a nav item by its automation id.</summary>
    private void SelectNavById(string id)
    {
        var item = NavItems.FirstOrDefault(n => n.Id == id);
        if (item != null) SelectedNav = item;
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

        // WIP placeholders
        WipResourceHistory?.Dispose();
        WipPrivacyMonitor?.Dispose();
        WipFileLockDetector?.Dispose();
        WipSettingsWatchdog?.Dispose();
        WipBandwidthMonitor?.Dispose();
        WipGamingProfile?.Dispose();
        WipStandbyListCleaner?.Dispose();
        WipTimerResolution?.Dispose();
        WipCpuAffinity?.Dispose();
        WipDisplayProfiles?.Dispose();
        WipFileShredder?.Dispose();
        WipScheduledMaintenance?.Dispose();
        WipDnsChanger?.Dispose();
        WipHostsEditor?.Dispose();
        WipPrivacySettings?.Dispose();
        WipDebloater?.Dispose();
        WipBrowserCleaner?.Dispose();
        WipEdgeOneDriveRemover?.Dispose();
        WipDefenderTweaks?.Dispose();
        WipNotificationBlocker?.Dispose();
        WipContextMenu?.Dispose();
        WipDarkModeScheduler?.Dispose();
        WipVolumeControl?.Dispose();
        WipEnvVariableEditor?.Dispose();
        WipTaskScheduler?.Dispose();
        WipBootAnalyzer?.Dispose();
        WipRestorePoints?.Dispose();
        WipProfileExportImport?.Dispose();
        WipCliInterface?.Dispose();
        WipSystemReport?.Dispose();

        GC.SuppressFinalize(this);
    }
}
