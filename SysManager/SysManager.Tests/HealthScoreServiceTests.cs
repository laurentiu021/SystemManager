// SysManager · HealthScoreServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="HealthScoreService"/> — validates scoring logic
/// for each component and the aggregation formula.
/// </summary>
public class HealthScoreServiceTests
{
    /// <summary>The drive letter these tests treat as the system drive.</summary>
    /// <remarks>
    /// A literal, and deliberately not <see cref="HealthScoreService.SystemDriveLetter"/>: a test that asked
    /// the environment which drive Windows is on would pass for the wrong reason on a machine that boots from
    /// D:, and would stop testing the letter-matching at all.
    /// </remarks>
    private const string SystemDrive = "C:";

    /// <summary>A comfortable, readable system drive.</summary>
    /// <remarks>
    /// Used by the tests about OTHER components, so that adding free space to
    /// <see cref="HealthScoreService.UnavailableComponents"/> did not silently turn a test about the disk into
    /// a test about two things.
    /// </remarks>
    private static IReadOnlyList<FixedDriveService.FixedDrive> ReadableSystemDrive(
        double sizeGb = 500, double freeGb = 250) =>
        [new(SystemDrive, SystemDrive, "NTFS", sizeGb, freeGb, "", "")];

    // ---------- construction ----------

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var svc = new HealthScoreService(
            new SystemInfoService(), new DiskHealthService(), new BatteryService());
        Assert.NotNull(svc);
    }

    // ---------- ComputeDiskScore ----------

    [Fact]
    public void ComputeDiskScore_NullDisks_ScoresUnknownNotPerfect()
    {
        // This test used to require 100. That requirement WAS the defect: DiskHealthService swallows WMI
        // failures and returns its partially-filled list, so a Storage namespace that is broken or
        // access-denied arrives here as nothing at all — and the Dashboard's green branch is `>= 90`, so a
        // machine whose disks were never read was told "All SMART indicators healthy".
        Assert.Equal(HealthScoreService.UnknownComponentScore, HealthScoreService.ComputeDiskScore(null));
    }

    [Fact]
    public void ComputeDiskScore_EmptyList_ScoresUnknownNotPerfect()
    {
        // The empty list is the case that actually happens in the field, and it used to be scored MORE
        // generously than a single unreadable drive (100 versus 80) despite being a stronger absence of
        // evidence. The service's own comment claimed the per-drive case was "the only case that reaches it".
        Assert.Equal(HealthScoreService.UnknownComponentScore, HealthScoreService.ComputeDiskScore([]));
    }

    [Fact]
    public void ComputeDiskScore_AllHealthy_Returns100()
    {
        var disks = new List<DiskHealthReport>
        {
            new() { HealthStatus = "Healthy" },
            new() { HealthStatus = "Healthy" }
        };
        Assert.Equal(100, HealthScoreService.ComputeDiskScore(disks));
    }

    [Fact]
    public void ComputeDiskScore_OneWarning_Returns60()
    {
        var disks = new List<DiskHealthReport>
        {
            new() { HealthStatus = "Healthy" },
            new() { HealthStatus = "Warning" }
        };
        Assert.Equal(60, HealthScoreService.ComputeDiskScore(disks));
    }

    [Fact]
    public void ComputeDiskScore_OneUnhealthy_Returns20()
    {
        var disks = new List<DiskHealthReport>
        {
            new() { HealthStatus = "Unhealthy" }
        };
        Assert.Equal(20, HealthScoreService.ComputeDiskScore(disks));
    }

    // ---------- ComputeRamScore ----------

    [Fact]
    public void ComputeRamScore_NullSnapshot_ScoresUnknownNotPerfect()
    {
        // Same rule as the disk arm: a snapshot that never arrived is not evidence of healthy memory.
        Assert.Equal(HealthScoreService.UnknownComponentScore, HealthScoreService.ComputeRamScore(null));
    }

    [Fact]
    public void ComputeRamScore_LowUsage_Returns100()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 40);
        Assert.Equal(100, HealthScoreService.ComputeRamScore(snapshot));
    }

    [Fact]
    public void ComputeRamScore_60Percent_Returns100()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 60);
        Assert.Equal(100, HealthScoreService.ComputeRamScore(snapshot));
    }

    [Fact]
    public void ComputeRamScore_75Percent_Returns75()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 75);
        Assert.Equal(75, HealthScoreService.ComputeRamScore(snapshot));
    }

    [Fact]
    public void ComputeRamScore_90Percent_Returns40()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 90);
        Assert.Equal(40, HealthScoreService.ComputeRamScore(snapshot));
    }

    [Fact]
    public void ComputeRamScore_98Percent_Returns10()
    {
        var snapshot = MakeSnapshot(ramUsedPct: 98);
        Assert.Equal(10, HealthScoreService.ComputeRamScore(snapshot));
    }

    // ---------- ComputeUptimeScore ----------

    [Fact]
    public void ComputeUptimeScore_NullSnapshot_ScoresUnknownNotPerfect()
    {
        // Rewritten with its three siblings. This test required the defect: a snapshot that never arrived is
        // not evidence of a freshly-rebooted machine, and scoring it perfect is how a machine whose reads all
        // failed reported full health with no advice.
        Assert.Equal(HealthScoreService.UnknownComponentScore, HealthScoreService.ComputeUptimeScore(null));
    }

    [Fact]
    public void ComputeUptimeScore_1Day_Returns100()
    {
        var snapshot = MakeSnapshot(uptimeDays: 1);
        Assert.Equal(100, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    [Fact]
    public void ComputeUptimeScore_5Days_Returns90()
    {
        var snapshot = MakeSnapshot(uptimeDays: 5);
        Assert.Equal(90, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    [Fact]
    public void ComputeUptimeScore_10Days_Returns70()
    {
        var snapshot = MakeSnapshot(uptimeDays: 10);
        Assert.Equal(70, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    [Fact]
    public void ComputeUptimeScore_20Days_Returns50()
    {
        var snapshot = MakeSnapshot(uptimeDays: 20);
        Assert.Equal(50, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    [Fact]
    public void ComputeUptimeScore_45Days_Returns15()
    {
        var snapshot = MakeSnapshot(uptimeDays: 45);
        Assert.Equal(15, HealthScoreService.ComputeUptimeScore(snapshot));
    }

    // ---------- ComputeBatteryScore ----------

    [Fact]
    public void ComputeBatteryScore_NullBattery_Returns100()
    {
        Assert.Equal(100, HealthScoreService.ComputeBatteryScore(null));
    }

    [Fact]
    public void ComputeBatteryScore_NoBattery_Returns100()
    {
        var battery = new BatteryInfo { HasBattery = false };
        Assert.Equal(100, HealthScoreService.ComputeBatteryScore(battery));
    }

    [Fact]
    public void ComputeBatteryScore_HealthyBattery_Returns100()
    {
        var battery = new BatteryInfo
        {
            HasBattery = true,
            DesignCapacityMWh = 50000,
            FullChargeCapacityMWh = 48000 // 96% health
        };
        Assert.Equal(100, HealthScoreService.ComputeBatteryScore(battery));
    }

    [Fact]
    public void ComputeBatteryScore_DegradedBattery_Returns80()
    {
        var battery = new BatteryInfo
        {
            HasBattery = true,
            DesignCapacityMWh = 50000,
            FullChargeCapacityMWh = 35000 // 70% health
        };
        Assert.Equal(80, HealthScoreService.ComputeBatteryScore(battery));
    }

    [Fact]
    public void ComputeBatteryScore_WornBattery_Returns55()
    {
        var battery = new BatteryInfo
        {
            HasBattery = true,
            DesignCapacityMWh = 50000,
            FullChargeCapacityMWh = 22000 // 44% health
        };
        Assert.Equal(55, HealthScoreService.ComputeBatteryScore(battery));
    }

    // ---------- HealthScoreResult model ----------

    [Theory]
    [InlineData(100, StatusColors.Good)]
    [InlineData(80, StatusColors.Good)]
    [InlineData(79, StatusColors.Warning)]
    [InlineData(50, StatusColors.Warning)]
    [InlineData(49, StatusColors.Bad)]
    [InlineData(0, StatusColors.Bad)]
    public void HealthScoreResult_ColorHex_MatchesScore(int score, string expectedColor)
    {
        var result = new HealthScoreResult { Score = score };
        Assert.Equal(expectedColor, result.ColorHex);
    }

    [Theory]
    [InlineData(95, "Excellent")]
    [InlineData(85, "Good")]
    [InlineData(65, "Fair")]
    [InlineData(45, "Needs attention")]
    [InlineData(20, "Poor")]
    public void HealthScoreResult_Label_MatchesScore(int score, string expectedLabel)
    {
        var result = new HealthScoreResult { Score = score };
        Assert.Equal(expectedLabel, result.Label);
    }

    [Fact]
    public void HealthRecommendation_CriticalSeverity_RedColor()
    {
        var rec = new HealthRecommendation { Message = "Test", Severity = "critical" };
        Assert.Equal(StatusColors.Bad, rec.ColorHex);
    }

    [Fact]
    public void HealthRecommendation_WarningSeverity_AmberColor()
    {
        var rec = new HealthRecommendation { Message = "Test", Severity = "warning" };
        Assert.Equal(StatusColors.Warning, rec.ColorHex);
    }

    // ---------- helpers ----------

    private static SystemSnapshot MakeSnapshot(double ramUsedPct = 50, int uptimeDays = 1)
    {
        double totalGB = 16;
        double usedGB = totalGB * ramUsedPct / 100;
        return new SystemSnapshot(
            new OsInfo("Windows 11", "10.0", "22631", TimeSpan.FromDays(uptimeDays), "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, 10),
            new MemoryInfo(totalGB, totalGB - usedGB, usedGB, ramUsedPct, new List<MemoryModule>()),
            new List<DiskInfo>(),
            DateTime.Now);
    }

    // ---------- The disk score honours the Windows verdict ----------

    [Fact]
    public void ComputeDiskScore_UnhealthyDiskWithCleanSmartData_IsNot100()
    {
        // The Dashboard used to report a perfect disk score for a drive the Disk Health tab was
        // telling the user to replace.
        var disks = new List<DiskHealthReport>
        {
            new() { HealthStatus = "Healthy", WearPercent = 0 },
            new() { HealthStatus = "Unhealthy", WearPercent = 0, TemperatureC = 35 },
        };

        Assert.Equal(20, HealthScoreService.ComputeDiskScore(disks));
    }

    [Fact]
    public void ComputeDiskScore_DiskWithNoDataAtAll_ScoresTheUnknownValue()
    {
        // HealthPercent is null only when there is neither SMART data nor a recognised status.
        // Absent evidence is not a clean bill of health, so it scores 80 rather than 100.
        var disks = new List<DiskHealthReport> { new() { HealthStatus = "Unknown to us" } };

        Assert.Equal(80, HealthScoreService.ComputeDiskScore(disks));
    }

    // ---------- UnavailableComponents ----------

    [Fact]
    public void UnavailableComponents_DrivesPresentButNoneReadable_MarksTheDiskUnavailable()
    {
        // The score alone cannot carry this. Two drives with no SMART data score the deliberate unknown 80,
        // and ClassifySmartHealth reads 80 with unavailable=false through its `>= 60` branch as "Disk health
        // degrading" — a claim about failing hardware on a machine where nothing was measured. The test
        // directly below UnknownComponentScore_StaysBelowEveryGreenBranch asserts that intent in prose
        // ("must not read as degrading either — nothing was measured") while this path delivered exactly that.
        var disks = new List<DiskHealthReport>
        {
            new() { FriendlyName = "Samsung SSD", HealthStatus = "" },
            new() { FriendlyName = "WDC HDD", HealthStatus = "" }
        };
        Assert.All(disks, d => Assert.Null(d.HealthPercent));   // the premise, not an assumption

        var unavailable = HealthScoreService.UnavailableComponents(disks, null, ReadableSystemDrive(), SystemDrive);

        Assert.Contains(HealthScoreService.DiskComponent, unavailable);
    }

    [Fact]
    public void UnavailableComponents_OneReadableDriveAmongUnreadable_KeepsTheDiskAvailable()
    {
        // The negative half, and the reason the rule is All rather than Any: marking the component
        // unavailable here would replace "Disk health critical" with "could not be read" and hide a drive
        // Windows has already flagged as failing.
        var disks = new List<DiskHealthReport>
        {
            new() { FriendlyName = "Failing drive", HealthStatus = "Unhealthy" },
            new() { FriendlyName = "Unreadable drive", HealthStatus = "" }
        };

        var unavailable = HealthScoreService.UnavailableComponents(disks, null, ReadableSystemDrive(), SystemDrive);

        Assert.DoesNotContain(HealthScoreService.DiskComponent, unavailable);
        Assert.Equal(20, HealthScoreService.ComputeDiskScore(disks));   // and the failing verdict survives
    }

    [Fact]
    public void UnavailableComponents_NoDrivesAtAll_StillMarksTheDiskUnavailable()
    {
        Assert.Contains(HealthScoreService.DiskComponent,
            HealthScoreService.UnavailableComponents([], null, ReadableSystemDrive(), SystemDrive));
        Assert.Contains(HealthScoreService.DiskComponent,
            HealthScoreService.UnavailableComponents(null, null, ReadableSystemDrive(), SystemDrive));
    }

    // ---------- free space: the component the score used to be missing ----------

    /// <summary>The verdict for a 200 GB system drive, across the range.</summary>
    /// <remarks>
    /// The three rules are not independently observable and that is deliberate, so this measures the COMBINED
    /// verdict rather than pretending to test the percentage table alone. A first draft did pretend, on a
    /// 100 GB drive, and got two rows wrong: 17% free is 17 GB, which the 20 GB floor pulls to 55 whatever the
    /// percentage says. Each row below states the arithmetic that produces it.
    /// </remarks>
    [Theory]
    [InlineData(60, 100)]   // 30% and 60 GB — both views comfortable
    [InlineData(40, 100)]   // 20%, exactly on the top percentage band
    [InlineData(30, 80)]    // 15% and 30 GB — both views say 80
    [InlineData(22, 55)]    // 11%: percentage 55, absolute silent, above the 20 GB floor
    [InlineData(20, 55)]    // 10% exactly, and exactly ON the floor rather than under it
    [InlineData(15, 25)]    //  7.5%: percentage 25, and the 20 GB floor caps at 55 — the lower wins
    [InlineData(10, 25)]    //  5% exactly, at the 10 GB floor rather than under it
    [InlineData(8, 10)]     //  4%: percentage 10; the 10 GB floor caps at 25 and cannot raise it
    public void ComputeFreeSpaceScore_ScoresTheSystemDriveAcrossTheRange(double freeGb, int expected)
        => Assert.Equal(expected, HealthScoreService.ComputeFreeSpaceScore(
            [new(SystemDrive, SystemDrive, "NTFS", 200, freeGb, "", "")], SystemDrive));

    /// <summary>
    /// A large disk is not unhealthy for having a small PERCENTAGE free.
    /// </summary>
    /// <remarks>
    /// 400 GB free on 4 TB is 10%, which the percentage table alone calls 55 — a "needs attention" verdict on
    /// a machine with nothing whatever wrong with it. This is the false alarm the absolute view exists to
    /// prevent, and the reason percent alone was rejected.
    /// </remarks>
    [Theory]
    [InlineData(4000, 400, 100)]   // 10% of 4 TB
    [InlineData(2000, 60, 100)]    //  3% of 2 TB, still 60 GB of room
    [InlineData(1000, 30, 80)]     //  3% of 1 TB, 30 GB — comfortable, not generous
    public void ComputeFreeSpaceScore_AGenerousAbsoluteAmountOverridesALowPercentage(
        double sizeGb, double freeGb, int expected)
        => Assert.Equal(expected, HealthScoreService.ComputeFreeSpaceScore(
            [new(SystemDrive, SystemDrive, "NTFS", sizeGb, freeGb, "", "")], SystemDrive));

    /// <summary>
    /// A small disk IS unhealthy for having little space, however good the percentage looks.
    /// </summary>
    /// <remarks>
    /// The other direction, and the reason absolute alone was rejected too. 9 GB free on a 40 GB drive is 22%
    /// — the top percentage band — and still not enough for Windows to install a feature update, which stages
    /// around 20 GB. The floor is what stops the percentage calling that healthy.
    /// </remarks>
    [Theory]
    [InlineData(40, 9, 25)]     // 22% free, under the 10 GB floor
    [InlineData(64, 15, 55)]    // 23% free, under the 20 GB floor
    [InlineData(32, 4, 25)]     // 12% free and nearly empty in absolute terms
    public void ComputeFreeSpaceScore_AHardFloorOverridesAHealthyPercentage(
        double sizeGb, double freeGb, int expected)
        => Assert.Equal(expected, HealthScoreService.ComputeFreeSpaceScore(
            [new(SystemDrive, SystemDrive, "NTFS", sizeGb, freeGb, "", "")], SystemDrive));

    /// <summary>Only the system drive counts.</summary>
    /// <remarks>
    /// A deliberately-packed archive disk does not slow Windows down, and scoring it would report a machine as
    /// unhealthy for a state its owner chose. The full D: here must not move the score at all.
    /// </remarks>
    [Fact]
    public void ComputeFreeSpaceScore_IgnoresAFullDataDrive()
    {
        IReadOnlyList<FixedDriveService.FixedDrive> drives =
        [
            new(SystemDrive, "Windows", "NTFS", 500, 250, "", ""),
            new("D:", "Archive", "NTFS", 4000, 1, "", "")
        ];

        Assert.Equal(100, HealthScoreService.ComputeFreeSpaceScore(drives, SystemDrive));
    }

    /// <summary>An unreadable system drive is unknown, not healthy.</summary>
    /// <remarks>
    /// The same rule every other component follows. <c>FixedDriveService.Enumerate</c> SKIPS a drive it cannot
    /// read — a BitLocker-locked volume throws on <c>AvailableFreeSpace</c> — so an unreadable system drive
    /// arrives here as an absence rather than an exception, and scoring an absence as 100 is exactly how the
    /// disk component once reported "All SMART indicators healthy" for a machine it had never read.
    /// </remarks>
    [Theory]
    [InlineData("D:")]    // enumerated drives exist, but not the system one
    [InlineData("")]      // the system drive could not be determined
    [InlineData(null)]
    public void ComputeFreeSpaceScore_SystemDriveMissing_IsUnknownRatherThanHealthy(string? systemDrive)
        => Assert.Equal(HealthScoreService.UnknownComponentScore,
            HealthScoreService.ComputeFreeSpaceScore(ReadableSystemDrive(), systemDrive));

    [Fact]
    public void ComputeFreeSpaceScore_NoDrivesAtAll_IsUnknownRatherThanHealthy()
    {
        Assert.Equal(HealthScoreService.UnknownComponentScore,
            HealthScoreService.ComputeFreeSpaceScore([], SystemDrive));
        Assert.Equal(HealthScoreService.UnknownComponentScore,
            HealthScoreService.ComputeFreeSpaceScore(null, SystemDrive));
    }

    /// <summary>A drive reporting zero size is not a measurement.</summary>
    /// <remarks>
    /// <c>SizeGB</c> is rounded to whole gigabytes, so a zero means either a drive too small to divide by or a
    /// read that produced nothing. Dividing by it would produce infinity and the top band.
    /// </remarks>
    [Fact]
    public void ComputeFreeSpaceScore_ZeroSizedDrive_IsUnknownRatherThanPerfect()
        => Assert.Equal(HealthScoreService.UnknownComponentScore,
            HealthScoreService.ComputeFreeSpaceScore(
                [new(SystemDrive, SystemDrive, "NTFS", 0, 0, "", "")], SystemDrive));

    /// <summary>The drive letter comparison ignores case.</summary>
    [Fact]
    public void ComputeFreeSpaceScore_MatchesTheDriveLetterCaseInsensitively()
        => Assert.Equal(100, HealthScoreService.ComputeFreeSpaceScore(
            [new("c:", "Windows", "NTFS", 500, 250, "", "")], "C:"));

    /// <summary>
    /// The system drive letter is derived, not assumed to be C:.
    /// </summary>
    /// <remarks>
    /// Shaped like <c>FixedDrive.Letter</c> — a letter and a colon, no trailing separator — because the score
    /// matches the two against each other. A trailing backslash here would silently match nothing and score
    /// every machine "unknown".
    /// </remarks>
    [Fact]
    public void SystemDriveLetter_IsALetterAndColonWithNoSeparator()
    {
        var letter = HealthScoreService.SystemDriveLetter();

        Assert.NotNull(letter);
        Assert.EndsWith(":", letter, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', letter);
        Assert.DoesNotContain('/', letter);
    }

    /// <summary>
    /// A full system drive cannot leave the overall score in a green band.
    /// </summary>
    /// <remarks>
    /// The defect in one assertion. With every other component perfect, a nearly-full system drive has to pull
    /// the total below "Excellent" (>= 90) and below "Good" (>= 80) — because before free space was a
    /// component, this exact machine reported "Excellent".
    /// <para>Goes through <see cref="HealthScoreService.Combine"/> rather than restating the weights, and that
    /// distinction is the whole value of the test. An earlier version computed the total from literal weights
    /// copied out of the service; a mutation that dropped free space back to a fifth — the very weighting this
    /// test was written to reject — went GREEN against it, because the test was measuring its own copy.
    /// <c>Combine</c> exists so there is one weighting and this reads it.</para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFullSystemDrive_CannotScoreGreen(bool hasBattery)
    {
        const int perfect = 100;
        var freeSpace = HealthScoreService.ComputeFreeSpaceScore(
            [new(SystemDrive, SystemDrive, "NTFS", 500, 2, "", "")], SystemDrive);
        Assert.Equal(10, freeSpace);   // the premise, not an assumption

        var overall = HealthScoreService.Combine(
            perfect, freeSpace, perfect, perfect, perfect, hasBattery);

        Assert.True(overall < 90, $"a machine out of space still scored {overall}, which reads as Excellent");
        Assert.True(overall < 80, $"a machine out of space still scored {overall}, which reads as Good");
    }

    /// <summary>Every component perfect scores exactly 100, on both arms.</summary>
    /// <remarks>
    /// Which is how the weights are proven to sum to one, through the code that uses them rather than through
    /// a copy of them. Weights that do not sum to one silently rescale every score in the app, and the battery
    /// arm redistributes so there are two sets to keep right — adding a fifth component is exactly when that
    /// gets broken.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryComponentPerfect_ScoresExactlyOneHundred(bool hasBattery)
        => Assert.Equal(100, HealthScoreService.Combine(100, 100, 100, 100, 100, hasBattery));

    /// <summary>Every component at zero scores zero, so no weight is silently negative or missing.</summary>
    /// <remarks>
    /// The other end of the same proof. Together with the 100 case this pins the weights to summing to exactly
    /// one: a missing component would leave the perfect case below 100, and a duplicated one would push the
    /// zero case above it.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryComponentZero_ScoresZero(bool hasBattery)
        => Assert.Equal(0, HealthScoreService.Combine(0, 0, 0, 0, 0, hasBattery));

    /// <summary>Free space is weighted at least as heavily as any component except disk health.</summary>
    /// <remarks>
    /// Stated as an inequality between measured outputs rather than as a number, so it survives a re-balance
    /// that keeps the intent. Feeding one component 0 and the rest 100 makes the drop equal to that
    /// component's weight, which is how the ordering is read out of <c>Combine</c> without naming a single
    /// weight here.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FreeSpace_WeighsAtLeastAsMuchAsRamUptimeAndBattery(bool hasBattery)
    {
        int Drop(int disk, int free, int ram, int uptime, int battery) =>
            100 - HealthScoreService.Combine(disk, free, ram, uptime, battery, hasBattery);

        var freeSpaceWeight = Drop(100, 0, 100, 100, 100);
        var ramWeight = Drop(100, 100, 0, 100, 100);
        var uptimeWeight = Drop(100, 100, 100, 0, 100);

        Assert.True(freeSpaceWeight >= ramWeight,
            $"free space moves the score by {freeSpaceWeight} and RAM by {ramWeight}; a full drive is the "
            + "commonest real problem and the only one with a remedy in this app");
        Assert.True(freeSpaceWeight >= uptimeWeight,
            $"free space moves the score by {freeSpaceWeight} and uptime by {uptimeWeight}");

        if (hasBattery)
        {
            var batteryWeight = Drop(100, 100, 100, 100, 0);
            Assert.True(freeSpaceWeight >= batteryWeight,
                $"free space moves the score by {freeSpaceWeight} and battery wear by {batteryWeight}");
        }
    }

    /// <summary>An unreadable system drive is reported as unavailable, so a caller can say why.</summary>
    [Fact]
    public void UnavailableComponents_UnreadableSystemDrive_MarksFreeSpaceUnavailable()
    {
        Assert.Contains(HealthScoreService.FreeSpaceComponent,
            HealthScoreService.UnavailableComponents(null, null, [], SystemDrive));

        // And the negative half: a readable system drive is not reported unavailable, or the reason would be
        // shown on every machine.
        Assert.DoesNotContain(HealthScoreService.FreeSpaceComponent,
            HealthScoreService.UnavailableComponents(null, null, ReadableSystemDrive(), SystemDrive));
    }

    /// <summary>A full DATA drive does not make free space unavailable, or unhealthy.</summary>
    /// <remarks>
    /// The rule here is "the system drive specifically", unlike the disk component's All-drives rule. A
    /// BitLocker-locked data volume is skipped by the enumeration and changes nothing about how much room
    /// Windows has, so reporting the component as unreadable because of it would be a false explanation.
    /// </remarks>
    [Fact]
    public void UnavailableComponents_DataDriveMissing_LeavesFreeSpaceAvailable()
    {
        IReadOnlyList<FixedDriveService.FixedDrive> onlySystem =
        [
            new(SystemDrive, "Windows", "NTFS", 500, 250, "", "")
        ];

        Assert.DoesNotContain(HealthScoreService.FreeSpaceComponent,
            HealthScoreService.UnavailableComponents(null, null, onlySystem, SystemDrive));
    }

    [Fact]
    public void UnknownComponentScore_StaysBelowEveryGreenBranch()
    {
        // The whole fix hangs on this one number, and the tests for the individual arms cannot pin it: they
        // compare the constant to itself, so raising it moves both sides of their assertion at once. Raising
        // it to 90 or above puts every unread component straight back into the Dashboard's green branch —
        // "All SMART indicators healthy" for a machine whose disks were never read — with no other code
        // changing anywhere.
        Assert.Equal(80, HealthScoreService.UnknownComponentScore);

        // Stated as the relationship as well as the value, because 90 is the Dashboard's green threshold and
        // that is the constraint that actually matters if either number is ever revisited.
        Assert.True(HealthScoreService.UnknownComponentScore < 90,
            "an unknown component must never reach a green verdict");
        Assert.True(HealthScoreService.UnknownComponentScore >= 60,
            "and must not read as degrading either — nothing was measured");
    }
}
