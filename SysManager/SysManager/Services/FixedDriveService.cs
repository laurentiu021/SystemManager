// SysManager · FixedDriveService — enumerate internal NTFS/ReFS volumes
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using System.Management;

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
        var drives = new List<FixedDrive>();
        foreach (var di in DriveInfo.GetDrives())
        {
            if (di.DriveType != DriveType.Fixed) continue;
            if (!di.IsReady) continue;

            var fs = (di.DriveFormat ?? string.Empty).ToUpperInvariant();
            if (fs != "NTFS" && fs != "REFS") continue;

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

        // Enrich with MSFT_PhysicalDisk media/bus info when possible.
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            var media = new Dictionary<string, (string Media, string Bus)>(StringComparer.OrdinalIgnoreCase);

            using var search = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject mo in search.Get())
            {
                var id = mo["DeviceId"]?.ToString() ?? "";
                media[id] = (
                    MapMedia(Convert.ToUInt32(mo["MediaType"] ?? 0u)),
                    MapBus(Convert.ToUInt32(mo["BusType"] ?? 0u)));
            }

            // We can't easily map DeviceId -> drive letter without another join,
            // so if we have exactly one disk we annotate everything with it.
            if (media.Count == 1)
            {
                var (m, b) = media.Values.First();
                for (var i = 0; i < drives.Count; i++)
                    drives[i] = drives[i] with { MediaType = m, BusType = b };
            }
        }
        catch
        {
            // Non-fatal — leave media/bus empty.
        }

        return drives;
    }

    private static string MapMedia(uint v) => v switch
    {
        3 => "HDD", 4 => "SSD", 5 => "SCM", _ => ""
    };

    private static string MapBus(uint v) => v switch
    {
        1 => "SCSI", 3 => "ATA", 7 => "USB", 10 => "SAS", 11 => "SATA", 17 => "NVMe", _ => ""
    };
}
