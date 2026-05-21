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
        // to avoid LineReceived event cross-talk between tabs.
        services.AddTransient<PowerShellRunner>();
        services.AddSingleton<SystemInfoService>();
        services.AddSingleton<WingetService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ShortcutCleanerService>();
        services.AddSingleton<DiskHealthService>();
        services.AddSingleton<BatteryService>();
        services.AddSingleton<TuneUpService>();
        services.AddSingleton<HealthScoreService>();
        services.AddSingleton<AppAlertService>();
        services.AddSingleton<AppBlockerService>();
        services.AddSingleton<DeepCleanupService>();
        services.AddSingleton<DiskAnalyzerService>();
        services.AddSingleton<DuplicateFileService>();
        services.AddSingleton<EventLogService>();
        services.AddSingleton<FixedDriveService>();
        services.AddSingleton<IconExtractorService>();
        services.AddSingleton<LargeFileScanner>();
        services.AddSingleton<MemoryTestService>();
        services.AddSingleton<NetworkRepairService>();
        services.AddSingleton<PerformanceService>();
        services.AddSingleton<PingMonitorService>();
        services.AddSingleton<ProcessManagerService>();
        services.AddSingleton<ServiceManagerService>();
        services.AddSingleton<SpeedTestHistoryService>();
        services.AddSingleton<SpeedTestService>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<TracerouteMonitorService>();
        services.AddSingleton<TracerouteService>();
        services.AddSingleton<UninstallerService>();
        services.AddSingleton<BulkInstallerService>();

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

        return services;
    }
}
