// SysManager · ServiceManagerService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.ServiceProcess;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Enumerates Windows services, provides gaming recommendations, and
/// allows starting/stopping/changing startup type. All mutations require admin.
/// </summary>
public class ServiceManagerService
{
    /// <summary>
    /// Gaming-oriented recommendations for common Windows services.
    /// Key = service name (case-insensitive), Value = (recommendation, reason).
    /// </summary>
    internal static readonly Dictionary<string, (string Rec, string Reason)> GamingGuide = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SysMain"] = ("safe-to-disable", "Superfetch — preloads apps into RAM. Disabling frees RAM for games and reduces disk I/O."),
        ["DiagTrack"] = ("safe-to-disable", "Connected User Experiences and Telemetry — sends usage data to Microsoft. No impact on functionality."),
        ["WSearch"] = ("safe-to-disable", "Windows Search indexer — uses CPU and disk in the background. Disable if you don't use Windows Search."),
        ["MapsBroker"] = ("safe-to-disable", "Downloaded Maps Manager — manages offline maps. Safe to disable if you don't use the Maps app."),
        ["Fax"] = ("safe-to-disable", "Windows Fax and Scan — unused on most gaming PCs."),
        ["RetailDemo"] = ("safe-to-disable", "Retail Demo Service — only used in store display mode."),
        ["WMPNetworkSvc"] = ("safe-to-disable", "Windows Media Player Network Sharing — shares media over the network. Rarely needed."),
        ["XblAuthManager"] = ("advanced", "Xbox Live Auth Manager — needed for Xbox Game Pass and Xbox Live features. Disable only if you don't use Xbox services."),
        ["XblGameSave"] = ("advanced", "Xbox Live Game Save — syncs game saves to Xbox Live. Disable only if you don't use Xbox cloud saves."),
        ["XboxGipSvc"] = ("advanced", "Xbox Accessory Management — manages Xbox controllers. Keep if you use Xbox controllers."),
        ["XboxNetApiSvc"] = ("advanced", "Xbox Live Networking — needed for Xbox multiplayer. Keep if you play Xbox games."),
        ["TabletInputService"] = ("safe-to-disable", "Touch Keyboard and Handwriting — safe to disable on desktops without touchscreens."),
        ["WbioSrvc"] = ("safe-to-disable", "Windows Biometric Service — fingerprint/face login. Disable if you don't use biometrics."),
        ["Spooler"] = ("safe-to-disable", "Print Spooler — manages print jobs. Disable if you don't have a printer."),
        ["RemoteRegistry"] = ("safe-to-disable", "Remote Registry — allows remote registry editing. Security risk, safe to disable."),
        ["lmhosts"] = ("safe-to-disable", "TCP/IP NetBIOS Helper — legacy name resolution. Safe to disable on modern networks."),
        ["Themes"] = ("keep-enabled", "Desktop themes and visual styles — disabling breaks the UI appearance."),
        ["AudioSrv"] = ("keep-enabled", "Windows Audio — required for all sound output."),
        ["Dhcp"] = ("keep-enabled", "DHCP Client — required for automatic IP address assignment."),
        ["Dnscache"] = ("keep-enabled", "DNS Client — caches DNS lookups for faster browsing."),
        ["EventLog"] = ("keep-enabled", "Windows Event Log — required for system diagnostics."),
        ["LanmanWorkstation"] = ("keep-enabled", "Workstation — required for network file sharing and SMB."),
        ["nsi"] = ("keep-enabled", "Network Store Interface — required for network connectivity."),
        ["Winmgmt"] = ("keep-enabled", "Windows Management Instrumentation — required by many apps and system tools."),
        ["wuauserv"] = ("keep-enabled", "Windows Update — keeps your system secure and up to date."),
    };

    /// <summary>
    /// Enumerate all Windows services with their current state and gaming recommendations.
    /// </summary>
    public static List<ServiceEntry> GetAllServices()
    {
        var services = ServiceController.GetServices();
        var result = new List<ServiceEntry>(services.Length);

        foreach (var sc in services)
        {
            try
            {
                var (rec, reason) = GamingGuide.TryGetValue(sc.ServiceName, out var guide)
                    ? guide
                    : ("keep-enabled", "");

                result.Add(new ServiceEntry
                {
                    Name = sc.ServiceName,
                    DisplayName = sc.DisplayName,
                    Description = GetServiceDescription(sc),
                    Status = sc.Status.ToString(),
                    StartType = sc.StartType.ToString(),
                    Recommendation = rec,
                    RecommendationReason = reason,
                });
            }
            catch (InvalidOperationException) { /* service disappeared — skip */ }
            finally { sc.Dispose(); }
        }

        return result.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Start a service. Requires admin.</summary>
    public static void StartService(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Stop a service. Requires admin.</summary>
    public static void StopService(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.CanStop && sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Change the startup type of a service via sc.exe. Requires admin.</summary>
    public static async Task SetStartupTypeAsync(string serviceName, string startType, PowerShellRunner ps, CancellationToken ct = default)
    {
        await ps.RunProcessAsync("sc.exe", $"config \"{serviceName}\" start= {startType}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Refresh the status of a single service entry.</summary>
    public static void RefreshStatus(ServiceEntry entry)
    {
        try
        {
            using var sc = new ServiceController(entry.Name);
            entry.Status = sc.Status.ToString();
            entry.StartType = sc.StartType.ToString();
        }
        catch (InvalidOperationException) { entry.Status = "Unknown"; }
    }

    private static string GetServiceDescription(ServiceController sc)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{sc.ServiceName}");
            return key?.GetValue("Description")?.ToString() ?? "";
        }
        catch (System.Security.SecurityException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
