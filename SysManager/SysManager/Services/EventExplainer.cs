// SysManager · EventExplainer
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Maps common Windows event IDs to plain-English explanations and
/// actionable recommendations. Lookup is (ProviderName, EventId) when
/// possible, falling back to EventId alone. When nothing matches we
/// produce a generic explanation from the severity and source.
/// </summary>
public static class EventExplainer
{
    private readonly record struct Key(string Provider, int EventId);

    private static readonly Dictionary<Key, (string Explanation, string Recommendation)> Known = new()
    {
        // ---------- Kernel / crashes ----------
        [new("Microsoft-Windows-Kernel-Power", 41)] = (
            "Your PC rebooted unexpectedly — Windows wasn't shut down cleanly. Usually means a power loss, BSOD, or a hard lock-up.",
            "Check Reliability Monitor for a BSOD around the same time. If frequent: update chipset/GPU drivers, test RAM (mdsched.exe), check PSU on desktops."),
        [new("BugCheck", 1001)] = (
            "Windows blue-screened (BSOD) and recovered. A memory dump was written.",
            "Open the .dmp file in WinDbg or use BlueScreenView to identify the driver. Update the implicated driver first."),
        [new("Microsoft-Windows-WER-SystemErrorReporting", 1001)] = (
            "A fault was reported to Windows Error Reporting — something crashed.",
            "Look at the earlier Application Error to see which process faulted."),
        [new("Application Error", 1000)] = (
            "A user-mode program crashed.",
            "Check the Faulting application and Faulting module fields. Reinstall the app or update it. If the module is a Windows DLL, run 'sfc /scannow'."),
        [new("Application Hang", 1002)] = (
            "A program stopped responding and Windows killed it.",
            "Usually benign if rare. If recurring for the same app: update or reinstall it."),
        [new(".NET Runtime", 1026)] = (
            "A .NET application crashed with an unhandled exception.",
            "The event includes the exception type and stack trace. Share it with the app's developer or check their bug tracker."),

        // ---------- Disk ----------
        [new("disk", 7)] = (
            "The disk had a bad block. Windows may have recovered the data, but the drive is showing signs of failure.",
            "Run 'chkdsk /f' on the affected drive. Back up important data now. If this repeats, the drive is dying — replace it."),
        [new("disk", 11)] = (
            "The driver detected a controller error — communication with a drive failed.",
            "Check cabling, then SMART status (e.g. with CrystalDiskInfo). Could be a failing drive or flaky SATA cable."),
        [new("disk", 51)] = (
            "Paging error while writing to disk.",
            "Often a sign of an overloaded or failing drive. Check SMART and free space on the system drive."),
        [new("Ntfs", 55)] = (
            "NTFS detected file-system corruption.",
            "Run 'chkdsk /f' (requires reboot). Back up data before."),
        [new("volmgr", 161)] = (
            "A dump file could not be written — pagefile too small or misconfigured.",
            "Increase pagefile size or enable automatic management in System Properties → Advanced → Performance."),

        // ---------- Network ----------
        [new("Microsoft-Windows-DNS-Client", 1014)] = (
            "DNS resolution timed out for a domain.",
            "Often transient. If frequent, try switching DNS to 1.1.1.1 / 8.8.8.8 or flush DNS ('ipconfig /flushdns')."),
        [new("Tcpip", 4227)] = (
            "TCP/IP had to close a connection due to repeated retransmissions.",
            "Usually a bad Wi-Fi signal or a flaky cable. Check physical link first."),
        [new("Microsoft-Windows-NetBT", 4321)] = (
            "Another device on the network is using the same computer name.",
            "Rename one of the machines. Use 'hostname' to see yours."),

        // ---------- Windows Update / Servicing ----------
        [new("Microsoft-Windows-WindowsUpdateClient", 20)] = (
            "A Windows Update failed to install.",
            "Run Windows Update Troubleshooter. If stuck, try 'wsreset.exe' and 'dism /online /cleanup-image /restorehealth'."),
        [new("Microsoft-Windows-WindowsUpdateClient", 25)] = (
            "A Windows Update was uninstalled.",
            "Informational — no action needed unless unexpected."),
        [new("Microsoft-Windows-Servicing", 3)] = (
            "A servicing operation failed. Component store may be inconsistent.",
            "Run 'dism /online /cleanup-image /restorehealth' then 'sfc /scannow'."),

        // ---------- Services ----------
        [new("Service Control Manager", 7000)] = (
            "A Windows service failed to start.",
            "Check the service's dependencies and logon account. Look at the previous events from the same source for the root cause."),
        [new("Service Control Manager", 7001)] = (
            "A service failed because a dependency didn't start.",
            "Fix the dependent service first."),
        [new("Service Control Manager", 7009)] = (
            "A service took too long to respond to the start request (30s default).",
            "Usually transient; if it persists, check the service or whether the disk is overloaded at boot."),
        [new("Service Control Manager", 7011)] = (
            "A timeout was reached when a service was stopping or starting.",
            "If it always happens on shutdown/startup, identify the slow service with the event details."),
        [new("Service Control Manager", 7023)] = (
            "A service terminated with a specific error code.",
            "The event contains the error code — decode with 'net helpmsg <code>'."),
        [new("Service Control Manager", 7031)] = (
            "A service terminated unexpectedly and was restarted.",
            "Look at the service's own logs (under Applications and Services Logs). Update or reinstall if it's third-party."),
        [new("Service Control Manager", 7034)] = (
            "A service terminated unexpectedly and was NOT restarted.",
            "Same as 7031 but without recovery. Check the service and its dependencies."),

        // ---------- Security / logon ----------
        [new("Microsoft-Windows-Security-Auditing", 4625)] = (
            "A logon attempt failed.",
            "Look at the Account Name and Source Network Address. Many hits from external IPs may indicate a brute-force attempt — block them at the firewall."),
        [new("Microsoft-Windows-Security-Auditing", 4740)] = (
            "A user account was locked out due to too many bad passwords.",
            "Find the source of the bad attempts — a stale mapped drive or saved credential is often the culprit."),

        // ---------- Display / GPU ----------
        [new("Display", 4101)] = (
            "The display driver stopped responding and was recovered (TDR).",
            "Update GPU drivers. If it happens under load, check temperatures and PSU. Occasional hits are normal."),
        [new("nvlddmkm", 153)] = (
            "The NVIDIA driver recovered from a hang (TDR).",
            "Update to the latest Game Ready or Studio driver. Check thermals."),
        [new("amdkmdag", 4101)] = (
            "The AMD driver stopped and recovered.",
            "Update Adrenalin drivers. Reset overclocks if applicable."),

        // ---------- Memory ----------
        [new("Microsoft-Windows-MemoryDiagnostics-Results", 1201)] = (
            "Windows Memory Diagnostic detected memory errors.",
            "Your RAM is likely faulty. Test each stick individually to find the culprit."),

        // ---------- Power / battery ----------
        [new("Microsoft-Windows-Kernel-Power", 42)] = (
            "The system is entering sleep.",
            "Informational."),
        [new("Microsoft-Windows-Power-Troubleshooter", 1)] = (
            "The system resumed from sleep.",
            "Informational."),
    };

    // Fallbacks by EventId only (when provider varies or isn't known)
    private static readonly Dictionary<int, (string Explanation, string Recommendation)> KnownById = new()
    {
        [41] = ("The PC wasn't shut down cleanly — crash, freeze, or power loss.",
                "See Reliability Monitor. Frequent = hardware/driver issue."),
        [1000] = ("A program crashed.", "Look at 'Faulting module' for the culprit."),
        [1001] = ("A crash report was filed.", "See the related Application Error for details."),
        [6008] = ("The previous system shutdown was unexpected.",
                  "Often a duplicate of Kernel-Power 41. Look for a BSOD or power loss around that time."),
    };

    public static void Enrich(FriendlyEventEntry entry)
    {
        if (TryLookup(entry, out var info))
        {
            entry.Explanation = info.Explanation;
            entry.Recommendation = info.Recommendation;
            return;
        }

        entry.Explanation = BuildGenericExplanation(entry);
        entry.Recommendation = BuildGenericRecommendation(entry);
    }

    private static bool TryLookup(FriendlyEventEntry e, out (string Explanation, string Recommendation) info)
    {
        if (Known.TryGetValue(new Key(e.ProviderName, e.EventId), out info)) return true;
        if (KnownById.TryGetValue(e.EventId, out info)) return true;
        info = default;
        return false;
    }

    private static string BuildGenericExplanation(FriendlyEventEntry e) => e.Severity switch
    {
        EventSeverity.Critical => $"Critical condition reported by '{e.ProviderName}'. The system or a core component is in trouble.",
        EventSeverity.Error    => $"An error was logged by '{e.ProviderName}'.",
        EventSeverity.Warning  => $"A warning from '{e.ProviderName}' — not necessarily broken, but worth a look if it repeats.",
        EventSeverity.Info     => $"Informational event from '{e.ProviderName}'.",
        _ => "Low-level diagnostic event."
    };

    private static string BuildGenericRecommendation(FriendlyEventEntry e) => e.Severity switch
    {
        EventSeverity.Critical => "Read the full message below. If it repeats, search the web for 'Event ID " + e.EventId + " " + e.ProviderName + "'.",
        EventSeverity.Error    => "If this keeps repeating, search for 'Event ID " + e.EventId + " " + e.ProviderName + "'.",
        EventSeverity.Warning  => "Usually safe to ignore one-offs. Check if it's a pattern.",
        _ => "No action needed."
    };
}
