using System.Management;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Collects system information via WMI / CIM (no PowerShell spawn needed).
/// </summary>
public class SystemInfoService
{
    public Task<SystemSnapshot> CaptureAsync(CancellationToken ct = default)
        => Task.Run(() => Capture(), ct);

    private SystemSnapshot Capture()
    {
        var os = QueryOs();
        var cpu = QueryCpu();
        var mem = QueryMemory();
        var disks = QueryDisks();
        return new SystemSnapshot(os, cpu, mem, disks, DateTime.Now);
    }

    private static OsInfo QueryOs()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime FROM Win32_OperatingSystem");
        foreach (ManagementObject mo in searcher.Get())
        {
            var caption = mo["Caption"]?.ToString() ?? "Windows";
            var version = mo["Version"]?.ToString() ?? "";
            var build = mo["BuildNumber"]?.ToString() ?? "";
            var arch = mo["OSArchitecture"]?.ToString() ?? "";
            var lastBootRaw = mo["LastBootUpTime"]?.ToString();
            var uptime = TimeSpan.Zero;
            if (!string.IsNullOrEmpty(lastBootRaw))
            {
                try { uptime = DateTime.Now - ManagementDateTimeConverter.ToDateTime(lastBootRaw); } catch (FormatException) { } catch (InvalidCastException) { }
            }
            return new OsInfo(caption, version, build, uptime, arch);
        }
        return new OsInfo("Windows", "", "", TimeSpan.Zero, "");
    }

    private static CpuInfo QueryCpu()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,LoadPercentage FROM Win32_Processor");
        foreach (ManagementObject mo in searcher.Get())
        {
            return new CpuInfo(
                mo["Name"]?.ToString()?.Trim() ?? "Unknown CPU",
                Convert.ToUInt32(mo["NumberOfCores"] ?? 0u),
                Convert.ToUInt32(mo["NumberOfLogicalProcessors"] ?? 0u),
                Convert.ToUInt32(mo["MaxClockSpeed"] ?? 0u),
                Convert.ToDouble(mo["LoadPercentage"] ?? 0.0));
        }
        return new CpuInfo("Unknown", 0, 0, 0, 0);
    }

    private static MemoryInfo QueryMemory()
    {
        double totalKb = 0, freeKb = 0;
        using (var s = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"))
        {
            foreach (ManagementObject mo in s.Get())
            {
                totalKb = Convert.ToDouble(mo["TotalVisibleMemorySize"] ?? 0);
                freeKb = Convert.ToDouble(mo["FreePhysicalMemory"] ?? 0);
            }
        }
        double totalGB = totalKb / 1024d / 1024d;
        double freeGB = freeKb / 1024d / 1024d;
        double usedGB = totalGB - freeGB;
        double pct = totalGB > 0 ? usedGB / totalGB * 100.0 : 0;

        var modules = new List<MemoryModule>();
        using (var s = new ManagementObjectSearcher("SELECT BankLabel,Manufacturer,Capacity,Speed,PartNumber FROM Win32_PhysicalMemory"))
        {
            foreach (ManagementObject mo in s.Get())
            {
                double capBytes = Convert.ToDouble(mo["Capacity"] ?? 0);
                modules.Add(new MemoryModule(
                    mo["BankLabel"]?.ToString() ?? "",
                    mo["Manufacturer"]?.ToString()?.Trim() ?? "",
                    capBytes / 1024d / 1024d / 1024d,
                    Convert.ToUInt32(mo["Speed"] ?? 0u),
                    mo["PartNumber"]?.ToString()?.Trim() ?? ""));
            }
        }
        return new MemoryInfo(totalGB, freeGB, usedGB, pct, modules);
    }

    private static List<DiskInfo> QueryDisks()
    {
        var list = new List<DiskInfo>();
        // Use Storage namespace for MSFT_PhysicalDisk (gives HealthStatus / MediaType)
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            var query = new ObjectQuery("SELECT FriendlyName,MediaType,BusType,Size,HealthStatus,OperationalStatus FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject mo in searcher.Get())
            {
                var size = Convert.ToDouble(mo["Size"] ?? 0) / 1024d / 1024d / 1024d;
                var mediaType = (ushort)Convert.ToUInt32(mo["MediaType"] ?? 0u) switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => "Unspecified"
                };
                var busType = (ushort)Convert.ToUInt32(mo["BusType"] ?? 0u) switch
                {
                    1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA",
                    6 => "Fibre", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS",
                    11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe",
                    _ => "Other"
                };
                var health = (ushort)Convert.ToUInt32(mo["HealthStatus"] ?? 0u) switch
                {
                    0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown"
                };
                var opStatus = mo["OperationalStatus"] is ushort[] arr && arr.Length > 0
                    ? string.Join(",", arr.Select(OpStatusName))
                    : "Unknown";
                list.Add(new DiskInfo(
                    mo["FriendlyName"]?.ToString() ?? "Disk",
                    mediaType, busType, size, health, opStatus, null, null));
            }
        }
        catch
        {
            // Fallback to Win32_DiskDrive if MSFT_PhysicalDisk isn't available
            using var s = new ManagementObjectSearcher("SELECT Model,Size,Status FROM Win32_DiskDrive");
            foreach (ManagementObject mo in s.Get())
            {
                var size = Convert.ToDouble(mo["Size"] ?? 0) / 1024d / 1024d / 1024d;
                list.Add(new DiskInfo(
                    mo["Model"]?.ToString() ?? "Disk",
                    "Unknown", "Unknown", size,
                    mo["Status"]?.ToString() ?? "Unknown",
                    "Unknown", null, null));
            }
        }
        return list;
    }

    private static string OpStatusName(ushort v) => v switch
    {
        1 => "Other", 2 => "Unknown", 3 => "OK", 4 => "Degraded",
        5 => "Stressed", 6 => "Predictive Failure", 7 => "Error", 8 => "Non-Recoverable Error",
        9 => "Starting", 10 => "Stopping", 11 => "Stopped", _ => $"Code {v}"
    };
}
