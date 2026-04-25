// SysManager · MainWindowViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

        // Views are instantiated lazily on first access — lets unit tests
        // construct the VM on an MTA thread without pulling WPF resources in.
        NavItems.Add(new NavItem { Id = "nav-dashboard",      Label = "Dashboard",      Glyph = "\uE80F", Content = Dashboard,     ViewType = typeof(Views.DashboardView) });
        NavItems.Add(new NavItem { Id = "nav-app-updates",    Label = "App updates",    Glyph = "\uE7B8", Content = AppUpdates,    ViewType = typeof(Views.AppUpdatesView) });
        NavItems.Add(new NavItem { Id = "nav-windows-update", Label = "Windows Update", Glyph = "\uE895", Content = WindowsUpdate, ViewType = typeof(Views.WindowsUpdateView) });
        NavItems.Add(new NavItem { Id = "nav-system-health",  Label = "System health",  Glyph = "\uE9D9", Content = SystemHealth,  ViewType = typeof(Views.SystemHealthView) });
        NavItems.Add(new NavItem { Id = "nav-cleanup",        Label = "Cleanup",        Glyph = "\uE74D", Content = Cleanup,       ViewType = typeof(Views.CleanupView) });
        NavItems.Add(new NavItem { Id = "nav-deep-cleanup",    Label = "Deep cleanup",   Glyph = "\uE81E", Content = DeepCleanup,   ViewType = typeof(Views.DeepCleanupView) });
        NavItems.Add(new NavItem { Id = "nav-startup",        Label = "Startup",        Glyph = "\uE7B5", Content = Startup,       ViewType = typeof(Views.StartupView) });
        NavItems.Add(new NavItem { Id = "nav-duplicates",     Label = "Duplicates",     Glyph = "\uE8C8", Content = DuplicateFile, ViewType = typeof(Views.DuplicateFileView) });
        NavItems.Add(new NavItem { Id = "nav-disk-analyzer",  Label = "Disk Analyzer",  Glyph = "\uE958", Content = DiskAnalyzer,    ViewType = typeof(Views.DiskAnalyzerView) });
        NavItems.Add(new NavItem { Id = "nav-processes",      Label = "Processes",      Glyph = "\uEBC4", Content = ProcessManager, ViewType = typeof(Views.ProcessManagerView) });
        NavItems.Add(new NavItem { Id = "nav-battery",        Label = "Battery",        Glyph = "\uEBA6", Content = BatteryHealth,  ViewType = typeof(Views.BatteryHealthView) });
        NavItems.Add(new NavItem { Id = "nav-uninstaller",    Label = "Uninstaller",    Glyph = "\uE738", Content = Uninstaller,   ViewType = typeof(Views.UninstallerView) });
        NavItems.Add(new NavItem { Id = "nav-performance",    Label = "Performance",    Glyph = "\uE945", Content = Performance,   ViewType = typeof(Views.PerformanceView) });
        NavItems.Add(new NavItem { Id = "nav-network",        Label = "Network",        Glyph = "\uE839", Content = Network,         ViewType = typeof(Views.NetworkView) });
        NavItems.Add(new NavItem { Id = "nav-services",       Label = "Services",       Glyph = "\uE912", Content = Services,        ViewType = typeof(Views.ServicesView) });
        NavItems.Add(new NavItem { Id = "nav-drivers",        Label = "Drivers",        Glyph = "\uE950", Content = Drivers,       ViewType = typeof(Views.DriversView) });
        NavItems.Add(new NavItem { Id = "nav-logs",           Label = "Logs",           Glyph = "\uE9F9", Content = Logs,          ViewType = typeof(Views.LogsView) });
        NavItems.Add(new NavItem { Id = "nav-about",          Label = "About",          Glyph = "\uE946", Content = About,         ViewType = typeof(Views.AboutView) });

        SelectedNav = NavItems[0];
    }

    [RelayCommand]
    private void OpenAboutTab()
    {
        var about = NavItems.FirstOrDefault(n => n.Id == "nav-about");
        if (about != null) SelectedNav = about;
    }

    [RelayCommand]
    private void OpenDeepCleanupTab()
    {
        var dc = NavItems.FirstOrDefault(n => n.Id == "nav-deep-cleanup");
        if (dc != null) SelectedNav = dc;
    }

    [RelayCommand]
    private void OpenDiskAnalyzerTab()
    {
        var da = NavItems.FirstOrDefault(n => n.Id == "nav-disk-analyzer");
        if (da != null) SelectedNav = da;
    }

    [RelayCommand]
    private void OpenDuplicatesTab()
    {
        var dup = NavItems.FirstOrDefault(n => n.Id == "nav-duplicates");
        if (dup != null) SelectedNav = dup;
    }
}
