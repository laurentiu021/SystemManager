// SysManager · FixedDriveService — enumerate internal NTFS/ReFS volumes
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Management;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// Enumerates fixed, NTFS/ReFS internal volumes suitable for <c>chkdsk /scan</c>.
/// Excludes removable USB drives, network shares and optical media.
/// </summary>
public sealed class FixedDriveService
{
    public sealed record FixedDrive(
        string Letter,
        string Label,
        string FileSystem,
        double SizeGB,
        double FreeGB,
        string MediaType,
        string BusType);

    public Task<IReadOnlyList<FixedDrive>> EnumerateAsync(CancellationToken ct = default)
        // Do not forward ct to Task.Run — Enumerate() is synchronous and fast;
        // a pre-cancelled token would throw before the delegate even runs.
        => Task.Run(() => Enumerate());

    public static IReadOnlyList<FixedDrive> Enumerate()
    {
        // Primary source: DriveInfo (fast, always works, no admin).
        List<FixedDrive> drives = [];
        var fixedReady = DriveInfo.GetDrives()
            .Where(di => di.DriveType == DriveType.Fixed && di.IsReady)
            .Where(di => (di.DriveFormat ?? string.Empty).ToUpperInvariant() is "NTFS" or "REFS");

        foreach (var di in fixedReady)
        {
            // DriveInfo getters (VolumeLabel/TotalSize/AvailableFreeSpace) hit the
            // volume and can throw IOException/UnauthorizedAccessException if it goes
            // away or is denied between the IsReady check and the read (e.g. a
            // BitLocker-locked or transiently-busy volume). Skip that one drive
            // instead of aborting enumeration of the rest.
            try
            {
                var letter = di.Name.TrimEnd('\\', '/');
                drives.Add(new FixedDrive(
                    Letter: letter,
                    Label: string.IsNullOrWhiteSpace(di.VolumeLabel) ? letter : di.VolumeLabel,
                    FileSystem: di.DriveFormat ?? "NTFS",
                    SizeGB: Math.Round(di.TotalSize / 1024d / 1024d / 1024d, 0),
                    FreeGB: Math.Round(di.AvailableFreeSpace / 1024d / 1024d / 1024d, 0),
                    MediaType: "",
                    BusType: ""));
            }
            catch (IOException ex) { Log.Debug(ex, "Skipped drive that became unavailable mid-enumeration"); }
            catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Skipped drive — access denied mid-enumeration"); }
        }

        // Enrich with MSFT_PhysicalDisk media/bus info when possible.
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            var media = new Dictionary<string, (string Media, string Bus)>(StringComparer.OrdinalIgnoreCase);

            using var search = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"));
            using var physCollection = search.Get();
            foreach (ManagementObject mo in physCollection)
            {
                using (mo)
                {
                    var id = mo["DeviceId"]?.ToString() ?? "";
                    media[id] = (
                        MapMedia(ToUInt32Safe(mo["MediaType"])),
                        MapBus(ToUInt32Safe(mo["BusType"])));
                }
            }

            // FUNC-M5: Map drive letters to physical disks via MSFT_Partition.
            // This works for multi-disk systems (not just single-disk).
            var letterToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var partSearch = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition WHERE DriveLetter IS NOT NULL"));
                using var partCollection = partSearch.Get();
                foreach (ManagementObject mo in partCollection)
                {
                    using (mo)
                    {
                        var letter = mo["DriveLetter"]?.ToString();
                        var diskNum = mo["DiskNumber"]?.ToString();
                        if (!string.IsNullOrEmpty(letter) && !string.IsNullOrEmpty(diskNum))
                            letterToDisk[letter + ":"] = diskNum;
                    }
                }
            }
            catch (ManagementException) { /* partition query not supported — fall through */ }

            for (var i = 0; i < drives.Count; i++)
            {
                var driveLetter = drives[i].Letter.TrimEnd('\\', '/');
                if (letterToDisk.TryGetValue(driveLetter, out var diskId) &&
                    media.TryGetValue(diskId, out var info))
                {
                    drives[i] = drives[i] with { MediaType = info.Media, BusType = info.Bus };
                }
                else if (media.Count == 1)
                {
                    // Fallback: single disk — annotate all drives with it.
                    var (m, b) = media.Values.First();
                    drives[i] = drives[i] with { MediaType = m, BusType = b };
                }
            }
        }
        catch (ManagementException)
        {
            // Non-fatal — leave media/bus empty.
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // scope.Connect() can throw COMException when the Storage WMI namespace is
            // unavailable (older/headless Windows). Non-fatal — leave media/bus empty.
        }

        return drives;
    }

    /// <summary>
    /// Converts a WMI property value to uint, treating both null and
    /// <see cref="DBNull"/> as 0. WMI returns <c>DBNull.Value</c> (not null) for
    /// absent properties, and <c>Convert.ToUInt32(DBNull.Value)</c> throws —
    /// which previously crashed drive enumeration on common hardware.
    /// </summary>
    internal static uint ToUInt32Safe(object? value)
    {
        if (value is null || value is DBNull) return 0u;
        try { return Convert.ToUInt32(value); }
        catch (InvalidCastException) { return 0u; }
        catch (FormatException) { return 0u; }
        catch (OverflowException) { return 0u; }
    }

    private static string MapMedia(uint v) => v switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => ""
    };

    private static string MapBus(uint v) => v switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        10 => "SAS",
        11 => "SATA",
        17 => "NVMe",
        _ => ""
    };
}
