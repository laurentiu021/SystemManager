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

public partial class MainWindowViewModel : ObservableObject
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
    public NetworkViewModel Network { get; }
    public DriversViewModel Drivers { get; }
    public LogsViewModel Logs { get; }
    public AboutViewModel About { get; }
    public ServicesViewModel Services { get; }

    /// <summary>Grouped sidebar tree (7 categories).</summary>
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
        Network = new NetworkViewModel();
        Drivers = new DriversViewModel(new PowerShellRunner());
        Logs = new LogsViewModel();
        About = new AboutViewModel();
        Services = new ServicesViewModel();

        IsElevated = AdminHelper.IsElevated();
        ElevationBadge = IsElevated ? "Administrator" : "Standard user";
        Title = IsElevated ? "SysManager — Administrator" : "SysManager";
        Log.Information("MainWindow initialized. Elevated: {IsElevated}", IsElevated);

        // ── Sidebar tree: 7 groups, 18 leaf items ──────────────────────
        // Views are instantiated lazily on first access — lets unit tests
        // construct the VM on an MTA thread without pulling WPF resources in.

        // 🏠 Dashboard (single-item group — renders flat)
        var grpDashboard = new NavGroup { Id = "grp-dashboard", Label = "Dashboard", Glyph = "\uE80F", Children = {
            new NavItem { Id = "nav-dashboard", Label = "Dashboard", Glyph = "\uE80F", Content = Dashboard, ViewType = typeof(Views.DashboardView) },
        }};

        // 🔧 System
        var grpSystem = new NavGroup { Id = "grp-system", Label = "System", Glyph = "\uE912", Children = {
            new NavItem { Id = "nav-system-health", Label = "System health",  Glyph = "\uE9D9", Content = SystemHealth,    ViewType = typeof(Views.SystemHealthView) },
            new NavItem { Id = "nav-performance",   Label = "Performance",    Glyph = "\uE945", Content = Performance,     ViewType = typeof(Views.PerformanceView) },
            new NavItem { Id = "nav-services",      Label = "Services",       Glyph = "\uE912", Content = Services,        ViewType = typeof(Views.ServicesView) },
            new NavItem { Id = "nav-startup",       Label = "Startup",        Glyph = "\uE7B5", Content = Startup,         ViewType = typeof(Views.StartupView) },
            new NavItem { Id = "nav-processes",     Label = "Processes",      Glyph = "\uEBC4", Content = ProcessManager,  ViewType = typeof(Views.ProcessManagerView) },
        }};

        // 🧹 Cleanup
        var grpCleanup = new NavGroup { Id = "grp-cleanup", Label = "Cleanup", Glyph = "\uE74D", Children = {
            new NavItem { Id = "nav-cleanup",       Label = "Quick cleanup",  Glyph = "\uE74D", Content = Cleanup,     ViewType = typeof(Views.CleanupView) },
            new NavItem { Id = "nav-deep-cleanup",  Label = "Deep cleanup",   Glyph = "\uE81E", Content = DeepCleanup, ViewType = typeof(Views.DeepCleanupView) },
        }};

        // 💾 Storage (Large File Finder moves here from Deep Cleanup — resolves #98)
        var grpStorage = new NavGroup { Id = "grp-storage", Label = "Storage", Glyph = "\uE958", Children = {
            new NavItem { Id = "nav-disk-analyzer", Label = "Disk Analyzer",  Glyph = "\uE958", Content = DiskAnalyzer,   ViewType = typeof(Views.DiskAnalyzerView) },
            new NavItem { Id = "nav-duplicates",    Label = "Duplicates",     Glyph = "\uE8C8", Content = DuplicateFile,  ViewType = typeof(Views.DuplicateFileView) },
        }};

        // 🌐 Network (already has internal sub-tabs: Ping/Traceroute/Speed/Repair)
        var grpNetwork = new NavGroup { Id = "grp-network", Label = "Network", Glyph = "\uE839", Children = {
            new NavItem { Id = "nav-network", Label = "Network", Glyph = "\uE839", Content = Network, ViewType = typeof(Views.NetworkView) },
        }};

        // 📦 Apps
        var grpApps = new NavGroup { Id = "grp-apps", Label = "Apps", Glyph = "\uE7B8", Children = {
            new NavItem { Id = "nav-app-updates",    Label = "App updates",    Glyph = "\uE7B8", Content = AppUpdates,    ViewType = typeof(Views.AppUpdatesView) },
            new NavItem { Id = "nav-windows-update", Label = "Windows Update", Glyph = "\uE895", Content = WindowsUpdate, ViewType = typeof(Views.WindowsUpdateView) },
            new NavItem { Id = "nav-uninstaller",    Label = "Uninstaller",    Glyph = "\uE738", Content = Uninstaller,   ViewType = typeof(Views.UninstallerView) },
        }};

        // ℹ️ Info
        var grpInfo = new NavGroup { Id = "grp-info", Label = "Info", Glyph = "\uE946", Children = {
            new NavItem { Id = "nav-drivers", Label = "Drivers", Glyph = "\uE950", Content = Drivers,       ViewType = typeof(Views.DriversView) },
            new NavItem { Id = "nav-battery", Label = "Battery", Glyph = "\uEBA6", Content = BatteryHealth, ViewType = typeof(Views.BatteryHealthView) },
            new NavItem { Id = "nav-logs",    Label = "Logs",    Glyph = "\uE9F9", Content = Logs,          ViewType = typeof(Views.LogsView) },
            new NavItem { Id = "nav-about",   Label = "About",   Glyph = "\uE946", Content = About,         ViewType = typeof(Views.AboutView) },
        }};

        NavGroups.Add(grpDashboard);
        NavGroups.Add(grpSystem);
        NavGroups.Add(grpCleanup);
        NavGroups.Add(grpStorage);
        NavGroups.Add(grpNetwork);
        NavGroups.Add(grpApps);
        NavGroups.Add(grpInfo);

        // Flat index for backward compat (Open*Tab commands, tests, automation).
        foreach (var g in NavGroups)
            foreach (var item in g.Children)
                NavItems.Add(item);

        SelectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        Log.Information("Tab navigated: {TabLabel}", value.Label);

        // Auto-expand the parent group when a child is selected.
        foreach (var g in NavGroups)
            if (g.Children.Contains(value))
            { g.IsExpanded = true; break; }
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
}
