// SysManager · StorageLabels
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// The one name for each storage bus and media type <c>MSFT_PhysicalDisk</c> reports, for every list that shows a
/// disk.
/// </summary>
/// <remarks>
/// System Health's three disk lists each had a table of their own, and the tables had drifted: a disk on a RAID
/// controller read "RAID" under Storage devices and on the SMART card but had no bus at all in the chkdsk list, and
/// an SD or eMMC card read "SD" or "MMC" in one list and "Other" in the next. What a list shows for a value with no
/// name is still its own choice, because the lists draw it differently: the chkdsk list hides an empty label, while
/// the SMART card's separators need a word.
/// </remarks>
public static class StorageLabels
{
    /// <summary>The word a list shows for a bus type with no name here. The System Report leaves it out.</summary>
    public const string OtherBus = "Other";

    /// <summary>The word a list shows for a media type with no name here. The System Report leaves it out.</summary>
    public const string UnspecifiedMedia = "Unspecified";

    /// <summary>The name of an <c>MSFT_PhysicalDisk.BusType</c> value, or <paramref name="unknown"/>.</summary>
    public static string Bus(uint busType, string unknown = OtherBus) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => unknown,
    };

    /// <summary>The name of an <c>MSFT_PhysicalDisk.MediaType</c> value, or <paramref name="unknown"/>.</summary>
    public static string Media(uint mediaType, string unknown = UnspecifiedMedia) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => unknown,
    };
}
