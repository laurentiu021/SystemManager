// SysManager · TemperatureServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using LibreHardwareMonitor.Hardware;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="TemperatureService.ApplyStorageNames"/> — the storage-name substitution that used to
/// display one drive's temperature under another drive's name — and for what the service makes of the sensors
/// it is handed, of a missing CPU temperature, and of a disk read that fails.
/// </summary>
/// <remarks>
/// Opening LibreHardwareMonitor needs administrator rights and real hardware, so that is not reached from a unit
/// test. What is read from the hardware once it is open is: <see cref="TemperatureService.ReadHardware"/> takes
/// stand-in hardware. So are the drive names and the drive temperatures, which come from a
/// <see cref="DiskHealthService"/> that a test can make read anything, a read that fails included (#2782).
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
        // DiskHealthService silently SKIPS any disk with an unreadable WMI field, which shifts every
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

    // ---------- what is read from the hardware (#2828) ----------

    private static ISensor Temperature(string name, float value)
    {
        var sensor = Substitute.For<ISensor>();
        sensor.Name.Returns(name);
        sensor.SensorType.Returns(SensorType.Temperature);
        sensor.Value.Returns(value);
        return sensor;
    }

    private static IHardware Hardware(HardwareType type, string name, params ISensor[] sensors)
    {
        var hardware = Substitute.For<IHardware>();
        hardware.HardwareType.Returns(type);
        hardware.Name.Returns(name);
        hardware.Sensors.Returns(sensors);
        hardware.SubHardware.Returns(Array.Empty<IHardware>());
        return hardware;
    }

    private static List<TemperatureReading> Read(bool includeStorage, params IHardware[] hardware)
    {
        using var service = new TemperatureService(new DiskHealthService(), skipHardwareInit: true);
        List<TemperatureReading> readings = [];
        service.ReadHardware(hardware, readings, includeStorage);
        return readings;
    }

    /// <summary>
    /// An Intel processor's "Distance to TjMax" sensors are never read as a temperature.
    /// </summary>
    /// <remarks>
    /// LibreHardwareMonitor reports one per core, of type Temperature, valued TjMax minus the core's temperature.
    /// "Hottest Core" was the highest of every sensor named Core, so these cores at 38 and 40 under a TjMax of
    /// 100 showed 62 °C. Built the way LibreHardwareMonitor builds an Intel hybrid CPU: Core Max and Core Average
    /// first, then the cores, the package, and the distances.
    /// </remarks>
    [Fact]
    public void ReadHardware_IntelDistanceToTjMax_IsNotTheHottestCore()
    {
        var cpu = Hardware(HardwareType.Cpu, "Test CPU",
            Temperature("Core Max", 40), Temperature("Core Average", 39),
            Temperature("P-Core #1", 38), Temperature("P-Core #2", 40),
            Temperature("CPU Package", 41),
            Temperature("P-Core #1 Distance to TjMax", 62), Temperature("P-Core #2 Distance to TjMax", 60));

        var readings = Read(includeStorage: false, cpu);

        Assert.Equal(2, readings.Count);
        Assert.Equal("CPU Package (Test CPU)", readings[0].SensorName);
        Assert.Equal(41.0, readings[0].TemperatureC);
        // The core by its own name: Core Max is the same value but does not say which core it is.
        Assert.Equal("Hottest Core (P-Core #2)", readings[1].SensorName);
        Assert.Equal(40.0, readings[1].TemperatureC);
    }

    /// <summary>
    /// With no package sensor, the CPU row is a core, not a distance.
    /// </summary>
    /// <remarks>
    /// The package falls back to the first sensor, and the distances are the case from the report: cores at 40
    /// and distances at 60 must give 40, which is also what Resource History records as the higher of the rows.
    /// </remarks>
    [Fact]
    public void ReadHardware_NoPackageSensor_FallsBackToACoreNotADistance()
    {
        var cpu = Hardware(HardwareType.Cpu, "Test CPU",
            Temperature("CPU Core #1 Distance to TjMax", 60), Temperature("CPU Core #1", 40),
            Temperature("CPU Core #2 Distance to TjMax", 65), Temperature("CPU Core #2", 35));

        var readings = Read(includeStorage: false, cpu);

        Assert.All(readings, r => Assert.DoesNotContain("Distance", r.SensorName, StringComparison.Ordinal));
        Assert.Equal(40.0, readings.Where(r => r.Component == "CPU").Max(r => r.TemperatureC));
    }

    /// <summary>
    /// A Zen or Zen+ processor's "Core (Tctl)" is its fan-control value, not its temperature.
    /// </summary>
    /// <remarks>
    /// On those processors Tctl carries an offset of 10 to 27 °C over the die, and LibreHardwareMonitor then
    /// reports "Core (Tdie)" beside it. The package fell back to Tctl, which was also the hottest "Core", so the
    /// card showed the offset value. A processor without the offset reports one "Core (Tctl/Tdie)", which stays.
    /// </remarks>
    [Fact]
    public void ReadHardware_AmdTctlWithAnOffset_ReadsTdie()
    {
        var offset = Hardware(HardwareType.Cpu, "Zen CPU", Temperature("Core (Tctl)", 70), Temperature("Core (Tdie)", 50));
        var plain = Hardware(HardwareType.Cpu, "Zen 2 CPU", Temperature("Core (Tctl/Tdie)", 55));

        var readings = Read(includeStorage: false, offset, plain);

        Assert.Equal(new (string, double?)[] { ("CPU Package (Zen CPU)", 50.0), ("CPU Package (Zen 2 CPU)", 55.0) },
            readings.Select(r => (r.SensorName, r.TemperatureC)).ToArray());
    }

    /// <summary>
    /// The fast path does not update storage, and the full read still does.
    /// </summary>
    /// <remarks>
    /// The background sampler asks for no storage, and the loop used to call Update() on every disk before
    /// throwing the reading away: a SMART pass-through on every drive every 10 seconds whenever SysManager ran
    /// elevated, window hidden or not.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadHardware_UpdatesStorage_OnlyWhenStorageIsRead(bool includeStorage)
    {
        var cpu = Hardware(HardwareType.Cpu, "Test CPU", Temperature("CPU Package", 45));
        var disk = Hardware(HardwareType.Storage, "Samsung SSD 990 PRO 2TB", Temperature("Temperature", 38));

        var readings = Read(includeStorage, cpu, disk);

        cpu.Received(1).Update();
        disk.Received(includeStorage ? 1 : 0).Update();
        Assert.Equal(includeStorage, readings.Any(r => r.Component == "Storage"));
    }

    /// <summary>
    /// A drive LibreHardwareMonitor cannot name is numbered, and marked as a placeholder for the names to replace.
    /// </summary>
    [Fact]
    public void ReadHardware_ADriveLhmCannotName_IsANumberedPlaceholder()
    {
        var named = Hardware(HardwareType.Storage, "Samsung SSD 990 PRO 2TB", Temperature("Temperature", 38));
        var unnamed = Hardware(HardwareType.Storage, "1", Temperature("Temperature", 31));

        var readings = Read(includeStorage: true, named, unnamed);

        Assert.Equal(new (string, bool)[] { ("Samsung SSD 990 PRO 2TB", false), ("Drive 2", true) },
            readings.Select(r => (r.SensorName, r.NameIsPlaceholder)).ToArray());
    }

    // ---------- a disk read that failed is not kept (#2782) ----------

    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when a test says so.</summary>
    /// <remarks>The same hand-written shape <c>NvidiaGpuReaderTests</c> uses.</remarks>
    private sealed class TestTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static readonly DiskHealthRead WindowsDidNotAnswer = new([], "The RPC server is unavailable.");

    private static DiskHealthReport Disk(string name, double? temperatureC = null) =>
        new() { FriendlyName = name, TemperatureC = temperatureC };

    /// <summary>A disk service over <paramref name="reads"/>, read in order, and how many reads it got.</summary>
    private static (DiskHealthService Service, Func<int> Reads) DisksReading(params DiskHealthRead[] reads)
    {
        var queue = new Queue<DiskHealthRead>(reads);
        var count = 0;
        return (new DiskHealthService(() => { count++; return queue.Dequeue(); }, _ => { }), () => count);
    }

    /// <summary>
    /// A disk read that failed leaves the drives unnamed for one poll, not for the session.
    /// </summary>
    /// <remarks>
    /// <see cref="DiskHealthService"/> reports a failed read as no drives rather than by throwing, and the names were
    /// kept with <c>??=</c>, so one failed first read, a WMI service still starting at logon, held the drives at
    /// "Drive 1" until SysManager was restarted.
    /// </remarks>
    [Fact]
    public async Task TheDriveNames_AreReadAgain_AfterAReadThatFailed()
    {
        var (disks, reads) = DisksReading(WindowsDidNotAnswer, new([Disk("Samsung SSD 990 PRO 2TB")]));
        using var service = new TemperatureService(disks, skipHardwareInit: true);
        List<TemperatureReading> first = [Storage("Drive 1", placeholder: true)];
        List<TemperatureReading> second = [Storage("Drive 1", placeholder: true)];

        await service.EnrichStorageNamesAsync(first);
        await service.EnrichStorageNamesAsync(second);

        Assert.Equal("Drive 1", first[0].SensorName);
        Assert.Equal("Samsung SSD 990 PRO 2TB", second[0].SensorName);
        Assert.Equal(2, reads());
    }

    /// <summary>
    /// A read cut short names nothing, even with as many drives as there are readings, and is not kept.
    /// </summary>
    /// <remarks>
    /// Paired by position, the one drive it read could be one with no temperature sensor, and the reading would carry
    /// another drive's name.
    /// </remarks>
    [Fact]
    public async Task TheDriveNames_FromAReadCutShort_AreNotUsed()
    {
        var (disks, reads) = DisksReading(new([Disk("WDC WD40EZAZ-00SF3B0")], "Time out"),
            new([Disk("Samsung SSD 990 PRO 2TB")]));
        using var service = new TemperatureService(disks, skipHardwareInit: true);
        List<TemperatureReading> first = [Storage("Drive 1", placeholder: true)];
        List<TemperatureReading> second = [Storage("Drive 1", placeholder: true)];

        await service.EnrichStorageNamesAsync(first);
        await service.EnrichStorageNamesAsync(second);

        Assert.Equal("Drive 1", first[0].SensorName);
        Assert.Equal("Samsung SSD 990 PRO 2TB", second[0].SensorName);
        Assert.Equal(2, reads());
    }

    /// <summary>The names from a read Windows answered in full are read once, and used at every poll.</summary>
    [Fact]
    public async Task TheDriveNames_FromAReadThatWorked_AreReadOnce()
    {
        var (disks, reads) = DisksReading(new DiskHealthRead([Disk("Samsung SSD 990 PRO 2TB")]));
        using var service = new TemperatureService(disks, skipHardwareInit: true);

        for (var poll = 0; poll < 3; poll++)
        {
            List<TemperatureReading> readings = [Storage("Drive 1", placeholder: true)];
            await service.EnrichStorageNamesAsync(readings);
            Assert.Equal("Samsung SSD 990 PRO 2TB", readings[0].SensorName);
        }

        Assert.Equal(1, reads());
    }

    /// <summary>
    /// Without administrator rights, a disk read that failed hides the drive temperatures for one poll, not for
    /// <see cref="TemperatureService.DiskTemperatureTtl"/>.
    /// </summary>
    /// <remarks>
    /// The read was kept whatever it found: the catches meant to stop a failed one being kept were for exceptions
    /// <see cref="DiskHealthService"/> never lets out.
    /// </remarks>
    [Fact]
    public async Task TheDriveTemperatures_AreReadAgainAtTheNextPoll_AfterAReadThatFailed()
    {
        var (disks, reads) = DisksReading(WindowsDidNotAnswer, new([Disk("Samsung SSD 990 PRO 2TB", 38)]));
        using var service = new TemperatureService(disks, skipHardwareInit: true, timeProvider: new TestTimeProvider());
        List<TemperatureReading> first = [];
        List<TemperatureReading> second = [];

        await service.ReadDiskTemperaturesAsync(first);
        await service.ReadDiskTemperaturesAsync(second);

        Assert.Empty(first);
        var drive = Assert.Single(second);
        Assert.Equal("Samsung SSD 990 PRO 2TB", drive.SensorName);
        Assert.Equal(38.0, drive.TemperatureC);
        Assert.Equal(2, reads());
    }

    /// <summary>A read cut short shows the drives it did read, and is read again at the next poll.</summary>
    [Fact]
    public async Task TheDriveTemperatures_FromAReadCutShort_AreShownButNotKept()
    {
        var (disks, reads) = DisksReading(new([Disk("Samsung SSD 990 PRO 2TB", 38)], "Time out"),
            new([Disk("Samsung SSD 990 PRO 2TB", 38), Disk("WDC WD40EZAZ-00SF3B0", 31)]));
        using var service = new TemperatureService(disks, skipHardwareInit: true, timeProvider: new TestTimeProvider());
        List<TemperatureReading> first = [];
        List<TemperatureReading> second = [];

        await service.ReadDiskTemperaturesAsync(first);
        await service.ReadDiskTemperaturesAsync(second);

        Assert.Equal(["Samsung SSD 990 PRO 2TB"], first.Select(r => r.SensorName));
        Assert.Equal(["Samsung SSD 990 PRO 2TB", "WDC WD40EZAZ-00SF3B0"], second.Select(r => r.SensorName));
        Assert.Equal(2, reads());
    }

    /// <summary>A read Windows answered in full is kept for the time-to-live, and read again after it.</summary>
    [Fact]
    public async Task TheDriveTemperatures_FromAReadThatWorked_AreKeptForTheTimeToLive()
    {
        var clock = new TestTimeProvider();
        var (disks, reads) = DisksReading(new([Disk("Samsung SSD 990 PRO 2TB", 38)]),
            new([Disk("Samsung SSD 990 PRO 2TB", 41)]));
        using var service = new TemperatureService(disks, skipHardwareInit: true, timeProvider: clock);
        List<TemperatureReading> first = [];
        List<TemperatureReading> kept = [];
        List<TemperatureReading> later = [];

        await service.ReadDiskTemperaturesAsync(first);
        clock.Advance(TemperatureService.DiskTemperatureTtl);
        await service.ReadDiskTemperaturesAsync(kept);
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.ReadDiskTemperaturesAsync(later);

        Assert.Equal(38.0, Assert.Single(kept).TemperatureC);
        Assert.Equal(41.0, Assert.Single(later).TemperatureC);
        Assert.Equal(2, reads());
    }

    // ---------- the CPU row when there is no CPU temperature (#2828) ----------

    /// <summary>
    /// Without the PawnIO driver the CPU row says so, elevated or not, and without elevation it says administrator.
    /// </summary>
    /// <remarks>
    /// LibreHardwareMonitor reads the CPU and the motherboard through PawnIO, installed separately. Elevated
    /// without it, the read had no CPU row at all, so the CPU temperature that running as administrator promised
    /// disappeared. Elevated with the driver and still no CPU temperature, nothing is added, as before.
    /// </remarks>
    [Theory]
    [InlineData(true, false, "Requires driver")]
    [InlineData(false, false, "Requires driver")]
    [InlineData(false, true, "Requires admin")]
    [InlineData(true, true, null)]
    public void AddCpuPlaceholder_NoCpuTemperature_SaysWhatIsMissing(bool isElevated, bool driverInstalled,
        string? expected)
    {
        List<TemperatureReading> readings = [new("GPU Core", "Test GPU", 47.0)];

        TemperatureService.AddCpuPlaceholder(readings, isElevated, driverInstalled);

        var cpu = readings.Where(r => r.Component == "CPU").ToList();
        if (expected is null)
        {
            Assert.Empty(cpu);
            return;
        }

        var row = Assert.Single(cpu);
        Assert.Null(row.TemperatureC);
        Assert.Equal(expected, row.DisplayValue);
        Assert.Equal(!driverInstalled, row.RequiresDriver);
    }

    /// <summary>
    /// A CPU temperature that was read is never joined by a placeholder.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void AddCpuPlaceholder_CpuTemperatureRead_AddsNothing(bool isElevated, bool driverInstalled)
    {
        List<TemperatureReading> readings = [new("CPU", "CPU Package (Test CPU)", 45.0)];

        TemperatureService.AddCpuPlaceholder(readings, isElevated, driverInstalled);

        Assert.Single(readings);
    }

    /// <summary>
    /// "Requires driver" wins over "Requires admin": elevation alone cannot read the CPU without the driver.
    /// </summary>
    [Fact]
    public void DisplayValue_RequiresDriver_TakesPrecedenceOverRequiresAdmin()
    {
        var reading = new TemperatureReading("CPU", "CPU Package", null, RequiresAdmin: true, RequiresDriver: true);

        Assert.Equal("Requires driver", reading.DisplayValue);
    }
}
