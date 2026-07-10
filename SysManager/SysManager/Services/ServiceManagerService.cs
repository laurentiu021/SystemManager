// SysManager · ServiceManagerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Frozen;
using System.ServiceProcess;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Enumerates Windows services, provides gaming recommendations, and
/// allows starting/stopping/changing startup type. All mutations require admin.
/// </summary>
public sealed partial class ServiceManagerService
{
    /// <summary>
    /// Gaming-oriented recommendations for common Windows services.
    /// Key = service name (case-insensitive), Value = (recommendation, reason).
    /// </summary>
    internal static readonly FrozenDictionary<string, (string Rec, string Reason)> GamingGuide = new Dictionary<string, (string Rec, string Reason)>(StringComparer.OrdinalIgnoreCase)
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
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Enumerate all Windows services with their current state and gaming recommendations.
    /// </summary>
    public static List<ServiceEntry> GetAllServices()
    {
        var services = ServiceController.GetServices();
        var result = new List<ServiceEntry>(services.Length);

        foreach (var sc in services)
        {
            using (sc)
            {
                try
                {
                    var (rec, reason) = GamingGuide.TryGetValue(sc.ServiceName, out var guide)
                        ? guide
                        : ("", "");

                    var (safety, safetyDesc) = SafetyDatabase.GetServiceSafety(sc.ServiceName);

                    result.Add(new ServiceEntry
                    {
                        Name = sc.ServiceName,
                        DisplayName = sc.DisplayName,
                        Description = GetServiceDescription(sc),
                        Status = sc.Status.ToString(),
                        StartType = sc.StartType.ToString(),
                        Recommendation = rec,
                        RecommendationReason = reason,
                        SafetyLevel = safety,
                        SafetyDescription = safetyDesc,
                    });
                }
                catch (InvalidOperationException) { /* service disappeared — skip */ }
            }
        }

        return result.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Start a service. Requires admin.</summary>
    public static async Task StartServiceAsync(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status == ServiceControllerStatus.Running ||
            sc.Status == ServiceControllerStatus.StartPending)
            return;

        try
        {
            sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new InvalidOperationException(
                $"Service '{serviceName}' did not start within 30 seconds. It may still be starting — check Services again in a moment.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // sc.Start() surfaces the underlying Win32 error (e.g. access denied,
            // dependency failure) as a Win32Exception. Normalize to the type the
            // ViewModel layer already handles.
            throw new InvalidOperationException(
                $"Could not start service '{serviceName}': {ex.Message}", ex);
        }
    }

    /// <summary>Stop a service. Requires admin.</summary>
    public static async Task StopServiceAsync(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.CanStop && sc.Status != ServiceControllerStatus.Stopped)
        {
            try
            {
                sc.Stop();
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Service '{serviceName}' did not stop within 30 seconds. It may still be stopping — check Services again in a moment.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // The service state can change between the CanStop check and Stop(),
                // or the caller may lack rights — sc.Stop() then throws Win32Exception.
                throw new InvalidOperationException(
                    $"Could not stop service '{serviceName}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>Change the startup type of a service via sc.exe. Requires admin.</summary>
    public static async Task SetStartupTypeAsync(string serviceName, string startType, IPowerShellRunner ps, CancellationToken ct = default)
    {
        // SEC-006: Strict allowlist for service names — alphanumeric, spaces,
        // hyphens, underscores, dots, and dollar signs only (covers all valid
        // Windows service names including instance names like MSSQL$INSTANCE).
        if (string.IsNullOrWhiteSpace(serviceName) ||
            !ServiceNamePattern().IsMatch(serviceName))
            throw new ArgumentException("Invalid service name.", nameof(serviceName));

        var allowedTypes = new[] { "auto", "delayed-auto", "demand", "disabled" };
        if (!allowedTypes.Contains(startType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid start type: {startType}", nameof(startType));

        var exit = await ps.RunProcessAsync("sc.exe", $"config \"{serviceName}\" start= {startType}", ct, PowerShellRunner.OemEncoding)
            .ConfigureAwait(false);

        // sc.exe config can fail even when elevated — e.g. a TrustedInstaller-owned
        // service returns exit 5 (Access denied). Fail loud so the caller reports the
        // real outcome instead of a false "set to Disabled" success.
        if (exit != 0)
            throw new InvalidOperationException(
                $"Could not change startup type of '{serviceName}' to '{startType}' (sc.exe exit code {exit}).");
    }

    /// <summary>
    /// Maps a <see cref="ServiceController.StartType"/> string (the
    /// <see cref="ServiceStartMode"/> name, e.g. "Automatic", "Manual", "Disabled")
    /// to the corresponding sc.exe <c>start=</c> token, so a disabled service can be
    /// re-enabled to its exact previous startup type instead of always to Manual.
    /// Falls back to "demand" (Manual) for "Disabled" or any unrecognized value, since
    /// re-enabling to Disabled would be a no-op.
    /// </summary>
    internal static string StartTypeToScToken(string? startType) => startType switch
    {
        "Automatic" => "auto",
        "Manual" => "demand",
        "Boot" => "boot",
        "System" => "system",
        _ => "demand",
    };

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
        // The status/start-type getters call into the SCM and can throw
        // Win32Exception (access denied, or the handle became invalid right after a
        // stop/disable). Mirror GetAllServices/RefreshAsync which already catch it,
        // so refreshing one entry after a mutation can't crash the command.
        catch (System.ComponentModel.Win32Exception) { entry.Status = "Unknown"; }
    }

    private static string GetServiceDescription(ServiceController sc)
    {
        try
        {
            // SEC-M6: Validate service name before interpolating into registry path.
            // Although ServiceController.GetServices() returns names from the SCM
            // (trusted source), we defensively reject names with path separators or
            // registry metacharacters to prevent registry path traversal.
            var name = sc.ServiceName;
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains('\\') || name.Contains('/') ||
                name.Contains('\0'))
                return "";

            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{name}");
            return key?.GetValue("Description")?.ToString() ?? "";
        }
        catch (System.Security.SecurityException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    // \A…\z (absolute anchors): ^…$ would accept a trailing newline in the service
    // name, which is then interpolated into the sc.exe command line.
    [System.Text.RegularExpressions.GeneratedRegex(@"\A[\w \-.$]+\z")]
    private static partial System.Text.RegularExpressions.Regex ServiceNamePattern();
}
