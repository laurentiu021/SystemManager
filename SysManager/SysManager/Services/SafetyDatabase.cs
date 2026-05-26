// SysManager · SafetyDatabase — curated safety ratings for services and features
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

public static class SafetyDatabase
{
    public static (SafetyLevel Level, string Description) GetServiceSafety(string serviceName)
    {
        if (SafeServices.TryGetValue(serviceName, out var safe))
            return (SafetyLevel.Safe, safe);
        if (CautionServices.TryGetValue(serviceName, out var caution))
            return (SafetyLevel.Caution, caution);
        if (CriticalServices.TryGetValue(serviceName, out var critical))
            return (SafetyLevel.Critical, critical);
        return (SafetyLevel.Critical, "Unknown service — treat as critical until verified.");
    }

    public static (SafetyLevel Level, string Description) GetFeatureSafety(string featureName)
    {
        if (SafeFeatures.TryGetValue(featureName, out var safe))
            return (SafetyLevel.Safe, safe);
        if (CautionFeatures.TryGetValue(featureName, out var caution))
            return (SafetyLevel.Caution, caution);
        if (CriticalFeatures.TryGetValue(featureName, out var critical))
            return (SafetyLevel.Critical, critical);
        return (SafetyLevel.Caution, "Check documentation before modifying.");
    }

    private static readonly Dictionary<string, string> SafeServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DiagTrack"] = "Connected User Experiences and Telemetry — sends diagnostic data to Microsoft. Disabling stops all telemetry.",
        ["dmwappushservice"] = "WAP Push Message Routing — used for telemetry delivery. Safe to disable.",
        ["SysMain"] = "Superfetch — prefetches apps into memory. Unnecessary on SSD, wastes write cycles.",
        ["WSearch"] = "Windows Search Indexer — indexes files for fast search. Disable if you use Everything or don't search in Explorer.",
        ["MapsBroker"] = "Downloaded Maps Manager — background map updates. Safe unless you use Windows Maps.",
        ["lfsvc"] = "Geolocation Service — tracks device location. Disable for privacy unless apps need it.",
        ["RetailDemo"] = "Retail Demo Service — only for store display PCs. Always safe to disable.",
        ["wisvc"] = "Windows Insider Service — only needed if in Insider Program.",
        ["TabletInputService"] = "Touch Keyboard and Handwriting — only needed on touchscreen devices.",
        ["Fax"] = "Windows Fax — only needed if you send faxes through a modem.",
        ["XblAuthManager"] = "Xbox Live Auth Manager — only needed for Xbox/Game Pass features.",
        ["XblGameSave"] = "Xbox Live Game Save — only needed for Xbox cloud saves.",
        ["XboxGipSvc"] = "Xbox Accessory Management — only needed for Xbox controllers via Xbox app.",
        ["XboxNetApiSvc"] = "Xbox Live Networking — only needed for Xbox multiplayer.",
        ["WMPNetworkSvc"] = "Windows Media Player Network Sharing — legacy media sharing. Rarely needed.",
        ["AxInstSV"] = "ActiveX Installer — legacy IE component. Not needed on modern browsers.",
        ["RemoteRegistry"] = "Remote Registry — allows remote registry editing. Security risk if enabled.",
        ["TrkWks"] = "Distributed Link Tracking Client — tracks NTFS links across network. Rarely needed.",
        ["WerSvc"] = "Windows Error Reporting — sends crash reports to Microsoft.",
        ["PhoneSvc"] = "Phone Service — manages telephony state. Not needed on desktops.",
    };

    private static readonly Dictionary<string, string> CautionServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wuauserv"] = "Windows Update — handles updates. Disabling prevents security patches. Only disable temporarily.",
        ["Spooler"] = "Print Spooler — required for printing. Disable only if you never print.",
        ["BITS"] = "Background Intelligent Transfer — used by Windows Update and Store. Disabling may break updates.",
        ["Themes"] = "Themes — provides visual themes. Disabling gives classic look but saves minimal resources.",
        ["AudioSrv"] = "Windows Audio — disabling removes all sound. Only disable on headless servers.",
        ["Audiosrv"] = "Windows Audio — disabling removes all sound.",
        ["Dhcp"] = "DHCP Client — auto-assigns IP. Disable only with static IP configured.",
        ["Dnscache"] = "DNS Client — caches DNS lookups. Disabling slows browsing.",
        ["EventLog"] = "Windows Event Log — disabling prevents all logging. Bad for troubleshooting.",
        ["LanmanServer"] = "Server — SMB file sharing. Disable if you don't share files on network.",
        ["LanmanWorkstation"] = "Workstation — SMB client. Disable if you don't access network shares.",
        ["Schedule"] = "Task Scheduler — many system tasks depend on this. Disabling can break maintenance.",
    };

    private static readonly Dictionary<string, string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RpcSs"] = "Remote Procedure Call — core Windows IPC. System will not function without it.",
        ["RpcEptMapper"] = "RPC Endpoint Mapper — required by RPC. Do not disable.",
        ["DcomLaunch"] = "DCOM Server Process Launcher — core component. Breaking this crashes Windows.",
        ["LSM"] = "Local Session Manager — manages user sessions. Disabling prevents login.",
        ["SamSs"] = "Security Accounts Manager — stores security credentials. Critical for authentication.",
        ["WinDefend"] = "Windows Defender Antivirus — primary security layer. Do not disable without replacement.",
        ["mpssvc"] = "Windows Defender Firewall — network protection. Disabling exposes all ports.",
        ["BFE"] = "Base Filtering Engine — core firewall engine. Required by Windows Firewall.",
        ["CryptSvc"] = "Cryptographic Services — handles certificates, Windows Update signatures. Breaking halts updates.",
        ["lsass"] = "Local Security Authority — authentication core. Cannot be disabled.",
        ["Winmgmt"] = "Windows Management Instrumentation — WMI is used by most management tools.",
        ["PlugPlay"] = "Plug and Play — device detection. Disabling breaks hardware recognition.",
        ["Power"] = "Power — manages power policy. Disabling causes unpredictable behavior.",
        ["ProfSvc"] = "User Profile Service — loads user profiles. Disabling prevents login.",
        ["nsi"] = "Network Store Interface — network connectivity core.",
    };

    private static readonly Dictionary<string, string> SafeFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Internet-Explorer-Optional-amd64"] = "Legacy IE compatibility in Edge. Not needed unless you use old enterprise sites.",
        ["MediaPlayback"] = "Windows Media Player legacy — replaced by modern Media Player app.",
        ["WindowsMediaPlayer"] = "Windows Media Player legacy codec pack. Modern apps don't need it.",
        ["Printing-XPSServices-Features"] = "XPS Document Writer — rarely used virtual printer.",
        ["Printing-PrintToPDFServices-Features"] = "Microsoft Print to PDF — useful but can reinstall if needed.",
        ["WorkFolders-Client"] = "Work Folders — enterprise file sync. Not needed for personal use.",
        ["MicrosoftWindowsPowerShellV2Root"] = "PowerShell 2.0 — legacy version, security risk. Use PowerShell 7+.",
        ["MicrosoftWindowsPowerShellV2"] = "PowerShell 2.0 Engine — legacy, unnecessary with modern PS.",
        ["MSRDC-Infrastructure"] = "Remote Desktop Client — only needed if you RDP into other machines.",
        ["TelnetClient"] = "Telnet Client — insecure protocol. Use SSH instead.",
        ["TFTP"] = "TFTP Client — trivial file transfer. Rarely needed.",
        ["DirectPlay"] = "DirectPlay — legacy gaming API. Only needed for very old games.",
        ["SimpleTCP"] = "Simple TCP/IP Services — echo, daytime servers. Never needed on workstations.",
        ["SMB1Protocol"] = "SMB 1.0 — insecure file sharing protocol. Disable unless connecting to ancient NAS.",
    };

    private static readonly Dictionary<string, string> CautionFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NetFx4-AdvSrvs"] = ".NET Framework 4.x Advanced Services — some apps depend on this.",
        ["NetFx3"] = ".NET Framework 3.5 — needed by some older applications.",
        ["SearchEngine-Client-Package"] = "Windows Search — disabling removes Start menu and Explorer search.",
        ["Printing-Foundation-Features"] = "Print and Document Services — needed for any printing.",
        ["SmbDirect"] = "SMB Direct (RDMA) — high-speed file transfer. Only needed with RDMA NICs.",
        ["WCF-Services45"] = "WCF Services — .NET communication framework. Some enterprise apps need it.",
        ["Microsoft-Windows-Subsystem-Linux"] = "WSL — Windows Subsystem for Linux. Needed by developers using Linux tools.",
    };

    private static readonly Dictionary<string, string> CriticalFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft-Hyper-V-All"] = "Hyper-V — virtualization platform. Required by Docker, WSL2, Android Emulator.",
        ["Microsoft-Hyper-V"] = "Hyper-V core. Disabling breaks all VM-based tools.",
        ["VirtualMachinePlatform"] = "Virtual Machine Platform — required by WSL2 and Hyper-V.",
        ["HypervisorPlatform"] = "Windows Hypervisor Platform — needed by third-party VMs (VirtualBox, etc).",
        ["Containers"] = "Windows Containers — used by Docker. Disabling breaks container workloads.",
        ["Microsoft-Windows-Client-EmbeddedExp-Package"] = "Windows Sandbox — isolated test environment. Useful for security.",
    };
}
