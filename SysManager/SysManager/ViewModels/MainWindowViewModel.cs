// SysManager · MainWindowViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
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

    // ── Placeholder ViewModels for planned features (WIP) ──────────
    public PlaceholderViewModel WipWindowsFeatures { get; }
    public PlaceholderViewModel WipResourceHistory { get; }
    public AppAlertsViewModel AppAlerts { get; }
    public PlaceholderViewModel WipPrivacyMonitor { get; }
    public ShortcutCleanerViewModel ShortcutCleaner { get; }
    public PlaceholderViewModel WipFileShredder { get; }
    public PlaceholderViewModel WipDnsChanger { get; }
    public PlaceholderViewModel WipHostsEditor { get; }
    public PlaceholderViewModel WipBulkInstaller { get; }
    public AppBlockerViewModel AppBlocker { get; }
    public PlaceholderViewModel WipPrivacySettings { get; }
    public PlaceholderViewModel WipContextMenu { get; }
    public PlaceholderViewModel WipRestorePoints { get; }
    public PlaceholderViewModel WipScheduledMaintenance { get; }
    public PlaceholderViewModel WipSystemReport { get; }

    /// <summary>Grouped sidebar tree (9 categories).</summary>
    public ObservableCollection<NavGroup> NavGroups { get; } = new();

    /// <summary>Flat list of every leaf NavItem (backward compat + lookup).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private string _title = "SysManager";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _elevationBadge = "";

    public MainWindowViewModel()
    {
        var sysInfo = new SystemInfoService();
        var winget = new WingetService(new PowerShellRunner());        Dashboard = new DashboardViewModel(sysInfo);
        AppUpdates = new AppUpdatesViewModel(winget);
        WindowsUpdate = new WindowsUpdateViewModel(new PowerShellRunner());
        SystemHealth = new SystemHealthViewModel(sysInfo);
        Cleanup = new CleanupViewModel(new PowerShellRunner());
        DeepCleanup = new DeepCleanupViewModel();
        DuplicateFile = new DuplicateFileViewModel();
        DiskAnalyzer = new DiskAnalyzerViewModel();
        ProcessManager = new ProcessManagerViewModel();
        BatteryHealth = new BatteryHealthViewModel();
        Uninstaller = new UninstallerViewModel(new PowerShellRunner());
        Performance = new PerformanceViewModel(new PowerShellRunner());
        Startup = new StartupViewModel();
        NetworkShared = new NetworkSharedState();
        Ping = new PingViewModel(NetworkShared);
        Traceroute = new TracerouteViewModel(NetworkShared);
        SpeedTest = new SpeedTestViewModel(NetworkShared);
        NetworkRepair = new NetworkRepairViewModel(NetworkShared);
        Drivers = new DriversViewModel(new PowerShellRunner());
        Logs = new LogsViewModel();
        About = new AboutViewModel();
        Services = new ServicesViewModel();

        // ── WIP placeholders for planned features ──────────────────────
        WipWindowsFeatures = new PlaceholderViewModel("Windows Features", "Toggle Windows optional features on or off.", "388");
        WipResourceHistory = new PlaceholderViewModel("Resource History", "Historical CPU, RAM, GPU and temperature graphs.", "377");
        AppAlerts = new AppAlertsViewModel();
        WipPrivacyMonitor = new PlaceholderViewModel("Privacy Monitor", "Monitor and alert on webcam, microphone, and location access.", "380");
        ShortcutCleaner = new ShortcutCleanerViewModel();
        WipFileShredder = new PlaceholderViewModel("File Shredder", "Securely delete files beyond recovery.", "386");
        WipDnsChanger = new PlaceholderViewModel("DNS Changer", "Quickly switch DNS servers for any network adapter.", "382");
        WipHostsEditor = new PlaceholderViewModel("Hosts Editor", "Edit the Windows hosts file with a friendly UI.", "382");
        WipBulkInstaller = new PlaceholderViewModel("Bulk Installer", "Install multiple applications at once via winget.", "387");
        AppBlocker = new AppBlockerViewModel();
        WipPrivacySettings = new PlaceholderViewModel("Privacy Settings", "Windows debloat and privacy toggles.", "384");
        WipContextMenu = new PlaceholderViewModel("Context Menu", "Manage right-click context menu entries.", "385");
        WipRestorePoints = new PlaceholderViewModel("Restore Points", "Create and manage system restore points.", "383");
        WipScheduledMaintenance = new PlaceholderViewModel("Scheduled Maintenance", "Automate cleanup and maintenance tasks.", "383");
        WipSystemReport = new PlaceholderViewModel("System Report", "Comprehensive system info export.", "389");

        IsElevated = AdminHelper.IsElevated();
        ElevationBadge = IsElevated ? "Administrator" : "Standard user";
        Title = IsElevated ? "SysManager — Administrator" : "SysManager";
        Log.Information("MainWindow initialized. Elevated: {IsElevated}", IsElevated);

        // ── Sidebar tree: 9 groups, 36 leaf items ──────────────────────
        // Views are instantiated lazily on first access — lets unit tests
        // construct the VM on an MTA thread without pulling WPF resources in.

        // 🏠 Dashboard (single-item group — renders flat)
        var grpDashboard = new NavGroup { Id = "grp-dashboard", Label = "Dashboard", Glyph = "\uE80F", Children = {
            new NavItem { Id = "nav-dashboard", Label = "Dashboard", Glyph = "\uE80F", Content = Dashboard, ViewType = typeof(Views.DashboardView) },
        }};

        // 🔧 System (6)
        var grpSystem = new NavGroup { Id = "grp-system", Label = "System", Glyph = "\uE912",
            Subtitle = "Health · WinUpdate · Perf · Services · Startup · Features",
            Tooltip = "System Health\nWindows Update\nPerformance Mode\nServices\nStartup Manager\nWindows Features",
            Children = {
            new NavItem { Id = "nav-system-health",     Label = "System Health",     Glyph = "\uE9D9", Content = SystemHealth,       ViewType = typeof(Views.SystemHealthView) },
            new NavItem { Id = "nav-windows-update",    Label = "Windows Update",    Glyph = "\uE895", Content = WindowsUpdate,      ViewType = typeof(Views.WindowsUpdateView) },
            new NavItem { Id = "nav-performance",       Label = "Performance Mode",  Glyph = "\uE945", Content = Performance,        ViewType = typeof(Views.PerformanceView) },
            new NavItem { Id = "nav-services",          Label = "Services",          Glyph = "\uE912", Content = Services,           ViewType = typeof(Views.ServicesView) },
            new NavItem { Id = "nav-startup",           Label = "Startup Manager",   Glyph = "\uE7B5", Content = Startup,            ViewType = typeof(Views.StartupView) },
            new NavItem { Id = "nav-windows-features",  Label = "Windows Features",  Glyph = "\uE9CE", Content = WipWindowsFeatures, ViewType = typeof(Views.PlaceholderView) },
        }};

        // 📊 Monitor (4) — NEW GROUP
        var grpMonitor = new NavGroup { Id = "grp-monitor", Label = "Monitor", Glyph = "\uE9D9",
            Subtitle = "Processes · Resources · Alerts · Privacy",
            Tooltip = "Process Manager\nResource History\nApp Alerts\nPrivacy Monitor",
            Children = {
            new NavItem { Id = "nav-processes",        Label = "Process Manager",  Glyph = "\uEBC4", Content = ProcessManager,    ViewType = typeof(Views.ProcessManagerView) },
            new NavItem { Id = "nav-resource-history", Label = "Resource History", Glyph = "\uE9D9", Content = WipResourceHistory, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-app-alerts",       Label = "App Alerts",       Glyph = "\uEA8F", Content = AppAlerts,          ViewType = typeof(Views.AppAlertsView) },
            new NavItem { Id = "nav-privacy-monitor",  Label = "Privacy Monitor",  Glyph = "\uE727", Content = WipPrivacyMonitor,  ViewType = typeof(Views.PlaceholderView) },
        }};

        // 🧹 Cleanup (4)
        var grpCleanup = new NavGroup { Id = "grp-cleanup", Label = "Cleanup", Glyph = "\uE74D",
            Subtitle = "Quick · Deep · Shortcuts · Shredder",
            Tooltip = "Quick Cleanup\nDeep Cleanup\nShortcut Cleaner\nFile Shredder",
            Children = {
            new NavItem { Id = "nav-cleanup",           Label = "Quick Cleanup",    Glyph = "\uE74D", Content = Cleanup,            ViewType = typeof(Views.CleanupView) },
            new NavItem { Id = "nav-deep-cleanup",      Label = "Deep Cleanup",     Glyph = "\uE81E", Content = DeepCleanup,        ViewType = typeof(Views.DeepCleanupView) },
            new NavItem { Id = "nav-shortcut-cleaner",  Label = "Shortcut Cleaner", Glyph = "\uE71B", Content = ShortcutCleaner, ViewType = typeof(Views.ShortcutCleanerView) },
            new NavItem { Id = "nav-file-shredder",     Label = "File Shredder",    Glyph = "\uE74D", Content = WipFileShredder,    ViewType = typeof(Views.PlaceholderView) },
        }};

        // 💾 Storage (2) — unchanged
        var grpStorage = new NavGroup { Id = "grp-storage", Label = "Storage", Glyph = "\uE958",
            Subtitle = "Disk Analyzer · Duplicate Finder",
            Tooltip = "Disk Analyzer\nDuplicate Finder",
            Children = {
            new NavItem { Id = "nav-disk-analyzer", Label = "Disk Analyzer",    Glyph = "\uE958", Content = DiskAnalyzer,  ViewType = typeof(Views.DiskAnalyzerView) },
            new NavItem { Id = "nav-duplicates",    Label = "Duplicate Finder", Glyph = "\uE8C8", Content = DuplicateFile, ViewType = typeof(Views.DuplicateFileView) },
        }};

        // 🌐 Network (6)
        var grpNetwork = new NavGroup { Id = "grp-network", Label = "Network", Glyph = "\uE839",
            Subtitle = "Ping · Traceroute · Speed · Repair · DNS · Hosts",
            Tooltip = "Ping\nTraceroute\nSpeed Test\nNetwork Repair\nDNS Changer\nHosts Editor",
            Children = {
            new NavItem { Id = "nav-ping",           Label = "Ping",           Glyph = "\uE839", Content = Ping,           ViewType = typeof(Views.PingView) },
            new NavItem { Id = "nav-traceroute",     Label = "Traceroute",     Glyph = "\uE8B0", Content = Traceroute,     ViewType = typeof(Views.TracerouteView) },
            new NavItem { Id = "nav-speed-test",     Label = "Speed Test",     Glyph = "\uE916", Content = SpeedTest,      ViewType = typeof(Views.SpeedTestView) },
            new NavItem { Id = "nav-network-repair", Label = "Network Repair", Glyph = "\uE90F", Content = NetworkRepair,  ViewType = typeof(Views.NetworkRepairView) },
            new NavItem { Id = "nav-dns-changer",    Label = "DNS Changer",    Glyph = "\uE968", Content = WipDnsChanger,  ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-hosts-editor",   Label = "Hosts Editor",   Glyph = "\uE8A5", Content = WipHostsEditor, ViewType = typeof(Views.PlaceholderView) },
        }};

        // 📦 Apps (4)
        var grpApps = new NavGroup { Id = "grp-apps", Label = "Apps", Glyph = "\uE7B8",
            Subtitle = "Updates · Installer · Uninstaller · Blocker",
            Tooltip = "App Updates\nBulk Installer\nUninstaller\nApp Blocker",
            Children = {
            new NavItem { Id = "nav-app-updates",    Label = "App Updates",     Glyph = "\uE7B8", Content = AppUpdates,      ViewType = typeof(Views.AppUpdatesView) },
            new NavItem { Id = "nav-bulk-installer", Label = "Bulk Installer",  Glyph = "\uE896", Content = WipBulkInstaller, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-uninstaller",    Label = "Uninstaller",     Glyph = "\uE738", Content = Uninstaller,     ViewType = typeof(Views.UninstallerView) },
            new NavItem { Id = "nav-app-blocker",    Label = "App Blocker",     Glyph = "\uE8F8", Content = AppBlocker,      ViewType = typeof(Views.AppBlockerView) },
        }};

        // 🛡️ Control (5) — NEW GROUP
        var grpControl = new NavGroup { Id = "grp-control", Label = "Control", Glyph = "\uE83D",
            Subtitle = "Privacy · Context Menu · Restore · Maintenance · Report",
            Tooltip = "Privacy Settings\nContext Menu\nRestore Points\nScheduled Maintenance\nSystem Report",
            Children = {
            new NavItem { Id = "nav-privacy-settings",      Label = "Privacy Settings",      Glyph = "\uE72E", Content = WipPrivacySettings,      ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-context-menu",          Label = "Context Menu",          Glyph = "\uE700", Content = WipContextMenu,          ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-restore-points",        Label = "Restore Points",        Glyph = "\uE7AD", Content = WipRestorePoints,        ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-scheduled-maintenance", Label = "Scheduled Maintenance", Glyph = "\uE823", Content = WipScheduledMaintenance, ViewType = typeof(Views.PlaceholderView) },
            new NavItem { Id = "nav-system-report",         Label = "System Report",         Glyph = "\uE9F9", Content = WipSystemReport,         ViewType = typeof(Views.PlaceholderView) },
        }};

        // ℹ️ Info (4)
        var grpInfo = new NavGroup { Id = "grp-info", Label = "Info", Glyph = "\uE946",
            Subtitle = "Drivers · Battery · Logs · About",
            Tooltip = "Drivers\nBattery Health\nSystem Logs\nAbout",
            Children = {
            new NavItem { Id = "nav-drivers", Label = "Drivers",        Glyph = "\uE950", Content = Drivers,       ViewType = typeof(Views.DriversView) },
            new NavItem { Id = "nav-battery", Label = "Battery Health", Glyph = "\uEBA6", Content = BatteryHealth, ViewType = typeof(Views.BatteryHealthView) },
            new NavItem { Id = "nav-logs",    Label = "System Logs",    Glyph = "\uE9F9", Content = Logs,          ViewType = typeof(Views.LogsView) },
            new NavItem { Id = "nav-about",   Label = "About",          Glyph = "\uE946", Content = About,         ViewType = typeof(Views.AboutView) },
        }};

        NavGroups.Add(grpDashboard);
        NavGroups.Add(grpSystem);
        NavGroups.Add(grpMonitor);
        NavGroups.Add(grpCleanup);
        NavGroups.Add(grpStorage);
        NavGroups.Add(grpNetwork);
        NavGroups.Add(grpApps);
        NavGroups.Add(grpControl);
        NavGroups.Add(grpInfo);

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
        WipWindowsFeatures?.Dispose();
        WipResourceHistory?.Dispose();
        AppAlerts?.Dispose();
        WipPrivacyMonitor?.Dispose();
        ShortcutCleaner?.Dispose();
        WipFileShredder?.Dispose();
        WipDnsChanger?.Dispose();
        WipHostsEditor?.Dispose();
        WipBulkInstaller?.Dispose();
        AppBlocker?.Dispose();
        WipPrivacySettings?.Dispose();
        WipContextMenu?.Dispose();
        WipRestorePoints?.Dispose();
        WipScheduledMaintenance?.Dispose();
        WipSystemReport?.Dispose();
        GC.SuppressFinalize(this);
    }
}
