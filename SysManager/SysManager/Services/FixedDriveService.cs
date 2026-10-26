// SysManager · FixedDriveService — enumerate internal NTFS/ReFS volumes
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Management;
using Serilog;
using SysManager.Helpers;

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

        // Filter only on DriveType/IsReady here, in that order: DriveType does not touch the
        // volume, so IsReady, which opens its root, is asked only of a fixed drive. Neither
        // throws. The DriveFormat check (which opens the volume handle and CAN throw
        // IOException on BitLocker-locked/transiently-busy drives) is deferred to inside the
        // per-drive try/catch below, so one flaky volume cannot abort enumeration of all
        // remaining drives.
        var fixedReady = DriveInfo.GetDrives()
            .Where(di => di.DriveType == DriveType.Fixed && di.IsReady);

        foreach (var di in fixedReady)
        {
            // DriveInfo getters (DriveFormat/VolumeLabel/TotalSize/AvailableFreeSpace)
            // hit the volume and can throw IOException/UnauthorizedAccessException if
            // it goes away or is denied between the IsReady check and the read (e.g. a
            // BitLocker-locked or transiently-busy volume). Skip that one drive
            // instead of aborting enumeration of the rest.
            try
            {
                var format = di.DriveFormat ?? string.Empty;
                if (format.ToUpperInvariant() is not ("NTFS" or "REFS"))
                    continue;

                drives.Add(Describe(di.Name, di.VolumeLabel, format, di.TotalSize, di.AvailableFreeSpace));
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

            using var search = Wmi.Searcher(scope, "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk");
            using var physCollection = search.Get();
            foreach (ManagementObject mo in physCollection)
            {
                using (mo)
                {
                    var id = mo["DeviceId"]?.ToString() ?? "";
                    // An empty label for a value with no name: the chkdsk list hides an empty one.
                    media[id] = (
                        StorageLabels.Media(ToUInt32Safe(mo["MediaType"]), unknown: ""),
                        StorageLabels.Bus(ToUInt32Safe(mo["BusType"]), unknown: ""));
                }
            }

            // FUNC-M5: Map drive letters to physical disks via MSFT_Partition.
            // This works for multi-disk systems (not just single-disk).
            var letterToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var partSearch = Wmi.Searcher(scope,
                    "SELECT DriveLetter, DiskNumber FROM MSFT_Partition WHERE DriveLetter IS NOT NULL");
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
        catch (UnauthorizedAccessException)
        {
            // WMI namespace access can be denied on locked-down/enterprise Windows. Non-fatal —
            // return the enumerated drives WITHOUT media/bus enrichment rather than discarding
            // the whole list (matches DiskHealthService's degrade-don't-throw handling).
        }

        return drives;
    }

    /// <summary>
    /// The record for one volume, from what its <see cref="DriveInfo"/> answered: the letter without its separator,
    /// the label or the letter when there is none, and the sizes in gigabytes as read.
    /// </summary>
    /// <remarks>
    /// The sizes are not rounded (#2802). The Health Score's free-space floors compare them, and rounded to whole
    /// gigabytes 9.7 GB free read as 10 and escaped the floor under 10, and 19.7 read as 20 and escaped the one under
    /// 20, so on a drive whose percentage looked healthy it drew no recommendation to free space. What shows a size
    /// rounds it for display.
    /// </remarks>
    internal static FixedDrive Describe(string name, string? label, string fileSystem, long totalBytes, long freeBytes)
    {
        var letter = name.TrimEnd('\\', '/');
        return new FixedDrive(
            Letter: letter,
            Label: string.IsNullOrWhiteSpace(label) ? letter : label,
            FileSystem: fileSystem,
            SizeGB: totalBytes / 1024d / 1024d / 1024d,
            FreeGB: freeBytes / 1024d / 1024d / 1024d,
            MediaType: "",
            BusType: "");
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

    internal static double ToDoubleSafe(object? value)
    {
        if (value is null || value is DBNull) return 0d;
        try { return Convert.ToDouble(value); }
        catch (InvalidCastException) { return 0d; }
        catch (FormatException) { return 0d; }
        catch (OverflowException) { return 0d; }
    }
}
