// SysManager · TemperatureServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="TemperatureService.ApplyStorageNames"/> — the storage-name substitution that used to
/// display one drive's temperature under another drive's name.
/// </summary>
/// <remarks>
/// The other three fixes in the same change are not reachable from a unit test and are not pretended to be:
/// opening LibreHardwareMonitor loads a kernel driver, the WMI disk-name query needs live WMI, and the
/// storage-temperature time-to-live sits behind <see cref="DiskHealthService"/>, which is sealed with a
/// non-virtual CollectAsync.
/// </remarks>
public class TemperatureServiceTests
{
    private static TemperatureReading Storage(string name, bool placeholder) =>
        new("Storage", name, 38.0, NameIsPlaceholder: placeholder);

    [Fact]
    public void ApplyStorageNames_GoodLhmName_IsNeverOverwritten()
    {
        // The defect. The substitution was unconditional, so a name LibreHardwareMonitor reported correctly was
        // replaced by whatever name sat at the same ordinal in an independently-ordered WMI list — and the two
        // lists are ordered by different things, with nothing correlating them. A user reads a healthy NVMe's
        // temperature under a failing HDD's name.
        List<TemperatureReading> readings = [Storage("Samsung SSD 990 PRO 2TB", placeholder: false)];

        TemperatureService.ApplyStorageNames(readings, ["WDC WD40EZAZ-00SF3B0"]);

        Assert.Equal("Samsung SSD 990 PRO 2TB", readings[0].SensorName);
    }

    [Fact]
    public void ApplyStorageNames_PlaceholderName_IsReplaced()
    {
        // The case the substitution exists for: LibreHardwareMonitor gave nothing usable, so a real model name
        // is an improvement over "Drive 1".
        List<TemperatureReading> readings = [Storage("Drive 1", placeholder: true)];

        TemperatureService.ApplyStorageNames(readings, ["Samsung SSD 990 PRO 2TB"]);

        Assert.Equal("Samsung SSD 990 PRO 2TB", readings[0].SensorName);
        Assert.False(readings[0].NameIsPlaceholder, "a real name is no longer a placeholder");
    }

    [Fact]
    public void ApplyStorageNames_CountsDisagree_SubstitutesNothing()
    {
        // An unequal count is proof the ordinal pairing is broken, and it breaks for a real reason:
        // DiskHealthService.Collect silently SKIPS any disk with an unreadable WMI field, which shifts every
        // later index by one. Two placeholders and one name means the one name might belong to either — and
        // keeping "Drive 1" is better than confidently showing the wrong model.
        List<TemperatureReading> readings =
        [
            Storage("Drive 1", placeholder: true),
            Storage("Drive 2", placeholder: true)
        ];

        TemperatureService.ApplyStorageNames(readings, ["Samsung SSD 990 PRO 2TB"]);

        Assert.Equal("Drive 1", readings[0].SensorName);
        Assert.Equal("Drive 2", readings[1].SensorName);
    }

    [Fact]
    public void ApplyStorageNames_MixedPlaceholders_ReplacesOnlyTheGuesses()
    {
        // The realistic case, and the one the two guards have to handle together: the counts line up, so
        // positions are usable, but only one of the two readings needs a name. Replacing both would clobber a
        // correct one; replacing neither would leave a placeholder that had a perfectly good name available.
        List<TemperatureReading> readings =
        [
            Storage("Samsung SSD 990 PRO 2TB", placeholder: false),
            Storage("Drive 2", placeholder: true)
        ];

        TemperatureService.ApplyStorageNames(readings, ["something else entirely", "WDC WD40EZAZ-00SF3B0"]);

        Assert.Equal("Samsung SSD 990 PRO 2TB", readings[0].SensorName);
        Assert.Equal("WDC WD40EZAZ-00SF3B0", readings[1].SensorName);
    }

    [Fact]
    public void ApplyStorageNames_BlankReplacement_LeavesThePlaceholder()
    {
        // A blank name is not an improvement on "Drive 1", and MSFT_PhysicalDisk does return empty friendly
        // names for some virtual and Storage Spaces devices.
        List<TemperatureReading> readings = [Storage("Drive 1", placeholder: true)];

        TemperatureService.ApplyStorageNames(readings, ["   "]);

        Assert.Equal("Drive 1", readings[0].SensorName);
    }

    [Fact]
    public void ApplyStorageNames_NonStorageRows_AreUntouched()
    {
        // CPU, GPU and motherboard rows share the list. The count comparison is over STORAGE rows only, so a
        // machine with three sensors and one disk must not be treated as a mismatch — and the non-storage rows
        // must never be renamed.
        List<TemperatureReading> readings =
        [
            new("CPU", "CPU Package", 55.0),
            Storage("Drive 1", placeholder: true),
            new("GPU", "GPU Core", 47.0)
        ];

        TemperatureService.ApplyStorageNames(readings, ["Samsung SSD 990 PRO 2TB"]);

        Assert.Equal("CPU Package", readings[0].SensorName);
        Assert.Equal("Samsung SSD 990 PRO 2TB", readings[1].SensorName);
        Assert.Equal("GPU Core", readings[2].SensorName);
    }

    [Fact]
    public void ApplyStorageNames_NoStorageRows_DoesNothingAndDoesNotThrow()
    {
        // A machine whose sensors expose no storage at all, with a disk list that is not empty. The old
        // index loop happened to survive this; the count guard has to as well.
        List<TemperatureReading> readings = [new("CPU", "CPU Package", 55.0)];

        TemperatureService.ApplyStorageNames(readings, ["Samsung SSD 990 PRO 2TB"]);

        Assert.Equal("CPU Package", readings[0].SensorName);
        Assert.Single(readings);
    }

    [Fact]
    public void DiskTemperatureTtl_IsLongEnoughToMatterAndShortEnoughToBeCurrent()
    {
        // The non-admin arm polls every 2 s and used to do a full Storage-namespace connect plus one SMART
        // association walk per disk on every tick. The point of the number is the ratio: it has to be many
        // times the poll interval to cut that work, and short enough that a warming drive still shows up.
        Assert.True(TemperatureService.DiskTemperatureTtl >= TimeSpan.FromSeconds(10),
            "a TTL near the 2 s poll interval would not reduce the WMI work it exists to reduce");
        Assert.True(TemperatureService.DiskTemperatureTtl <= TimeSpan.FromMinutes(2),
            "a drive warming up must still appear within a sensible time");
    }
}
