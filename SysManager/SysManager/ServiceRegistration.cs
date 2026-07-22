// SysManager · ServiceRegistration — DI container configuration
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Extensions.DependencyInjection;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager;

/// <summary>
/// Registers all services and ViewModels in the DI container.
/// Called once at application startup from <see cref="App.OnStartup"/>.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection ConfigureServices(this IServiceCollection services)
    {
        // ── Core services ──────────────────────────────────────────────
        // PowerShellRunner is Transient — each consumer gets its own instance
        // to avoid LineReceived event cross-talk between tabs. All consumers
        // depend on IPowerShellRunner (substitutable in tests via NSubstitute).
        services.AddTransient<IPowerShellRunner, PowerShellRunner>();
        services.AddSingleton<SystemInfoService>();
        // WingetService is Transient so each consuming ViewModel gets its own
        // IPowerShellRunner instance — avoids LineReceived cross-talk when
        // Dashboard and AppUpdates both run winget concurrently.
        services.AddTransient<IWingetService, WingetService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ShortcutCleanerService>();
        services.AddSingleton<DiskHealthService>();
        services.AddSingleton<TemperatureService>();
        services.AddSingleton<SystemReportService>();
        services.AddSingleton<BatteryService>();
        services.AddSingleton<TuneUpService>();
        services.AddSingleton<HealthScoreService>();
        services.AddSingleton<AppAlertService>();
        services.AddSingleton<IAppBlockerService, AppBlockerService>();
        services.AddSingleton<DeepCleanupService>();
        services.AddSingleton<DiskAnalyzerService>();
        services.AddSingleton<DuplicateFileService>();
        services.AddSingleton<EventLogService>();
        services.AddSingleton<FixedDriveService>();
        services.AddSingleton<LargeFileScanner>();
        services.AddSingleton<MemoryTestService>();
        services.AddSingleton<NetworkRepairService>();
        services.AddSingleton<PerformanceService>();
        services.AddSingleton<PingMonitorService>();
        services.AddSingleton<ProcessManagerService>();
        services.AddSingleton<SpeedTestHistoryService>();
        services.AddSingleton<SpeedTestService>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<TracerouteMonitorService>();
        services.AddSingleton<TracerouteService>();
        services.AddSingleton<UninstallerService>();
        services.AddSingleton<BulkInstallerService>();
        services.AddSingleton<AppIconService>();
        services.AddSingleton<FileShredderService>();
        services.AddSingleton<PrivacyService>();
        services.AddSingleton<DnsService>();
        services.AddSingleton<HostsFileService>();
        services.AddSingleton<ContextMenuService>();
        services.AddSingleton<EnvironmentVariableService>();
        services.AddSingleton<RestorePointService>();
        services.AddSingleton<DebloaterService>();
        services.AddSingleton<EdgeOneDriveService>();
        services.AddSingleton<LegacyPanelService>();
        services.AddSingleton<SystemFixService>();
        services.AddSingleton<ProfileService>();
        services.AddSingleton<BiosService>();
        services.AddSingleton<WindowsUpdatePolicyService>();
        services.AddSingleton<BrowserCleanerService>();
        services.AddSingleton<PrivacyMonitorService>();
        services.AddSingleton<BootAnalyzerService>();
        services.AddSingleton<WindowsUpdateService>();
        services.AddSingleton<ITimerResolutionService, TimerResolutionService>();
        services.AddSingleton<IFileLockService, FileLockService>();
        services.AddSingleton<DisplayProfileService>();
        services.AddSingleton<ICpuAffinityService, CpuAffinityService>();
        services.AddSingleton<DefenderService>();
        services.AddSingleton<TaskSchedulerService>();
        services.AddSingleton<IWindowsThemeService, WindowsThemeService>();
        services.AddSingleton<StandbyMemoryService>();
        services.AddSingleton<ResourceHistoryService>();
        services.AddSingleton<BandwidthHistoryService>();
        services.AddSingleton<ISettingsWatchdogService, SettingsWatchdogService>();
        services.AddSingleton<MaintenanceSchedulerService>();
        services.AddSingleton<ITweaksHubService, TweaksHubService>();
        services.AddSingleton<IAudioMixerService, AudioMixerService>();
        // Gaming Profile orchestrates the audited services above; it needs the process's
        // elevation state at construction (a value DI can't resolve), hence the factory.
        services.AddSingleton<IGamingProfileService>(sp => new GamingProfileService(
            sp.GetRequiredService<PerformanceService>(),
            sp.GetRequiredService<ITimerResolutionService>(),
            sp.GetRequiredService<ICpuAffinityService>(),
            sp.GetRequiredService<StandbyMemoryService>(),
            sp.GetRequiredService<RestorePointService>(),
            Helpers.AdminHelper.IsElevated()));

        // ── ViewModels (Singleton — one instance per tab) ──────────────
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<AppUpdatesViewModel>();
        services.AddSingleton<WindowsUpdateViewModel>();
        services.AddSingleton<SystemHealthViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<DeepCleanupViewModel>();
        services.AddSingleton<DuplicateFileViewModel>();
        services.AddSingleton<DiskAnalyzerViewModel>();
        services.AddSingleton<ProcessManagerViewModel>();
        services.AddSingleton<BatteryHealthViewModel>();
        services.AddSingleton<UninstallerViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<StartupViewModel>();
        services.AddSingleton<NetworkSharedState>();
        services.AddSingleton<PingViewModel>();
        services.AddSingleton<TracerouteViewModel>();
        services.AddSingleton<SpeedTestViewModel>();
        services.AddSingleton<NetworkRepairViewModel>();
        services.AddSingleton<DriversViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<ServicesViewModel>();
        services.AddSingleton<AppAlertsViewModel>();
        services.AddSingleton<ShortcutCleanerViewModel>();
        services.AddSingleton<WindowsFeaturesService>();
        services.AddSingleton<WindowsFeaturesViewModel>();
        services.AddSingleton<AppBlockerViewModel>();
        services.AddSingleton<BulkInstallerViewModel>();
        services.AddSingleton<FileShredderViewModel>();
        services.AddSingleton<PrivacyViewModel>();
        services.AddSingleton<DnsHostsViewModel>();
        services.AddSingleton<ContextMenuViewModel>();
        services.AddSingleton<SystemReportViewModel>();
        services.AddSingleton<EnvironmentVariablesViewModel>();
        services.AddSingleton<RestorePointsViewModel>();
        services.AddSingleton<DebloaterViewModel>();
        services.AddSingleton<EdgeOneDriveViewModel>();
        services.AddSingleton<LegacyPanelsViewModel>();
        services.AddSingleton<SystemFixesViewModel>();
        services.AddSingleton<ProfileViewModel>();
        services.AddSingleton<BrowserCleanerViewModel>();
        services.AddSingleton<PrivacyMonitorViewModel>();
        services.AddSingleton<BootAnalyzerViewModel>();
        services.AddSingleton<TimerResolutionViewModel>();
        services.AddSingleton<FileLockViewModel>();
        services.AddSingleton<DisplayProfileViewModel>();
        services.AddSingleton<CpuAffinityViewModel>();
        services.AddSingleton<DefenderViewModel>();
        services.AddSingleton<TaskSchedulerViewModel>();
        services.AddSingleton<DarkModeViewModel>();
        services.AddSingleton<StandbyMemoryViewModel>();
        services.AddSingleton<ResourceHistoryViewModel>();
        services.AddSingleton<BandwidthMonitorViewModel>();
        services.AddSingleton<SettingsWatchdogViewModel>();
        services.AddSingleton<CliInterfaceViewModel>();
        services.AddSingleton<ScheduledMaintenanceViewModel>();
        services.AddSingleton<TweaksHubViewModel>();
        services.AddSingleton<AudioMixerViewModel>();
        services.AddSingleton<GamingProfileViewModel>();

        return services;
    }
}
