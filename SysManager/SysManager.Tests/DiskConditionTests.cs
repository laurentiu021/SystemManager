// SysManager · DiskConditionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// One judgement of a drive, by the limits System Health's verdict uses, for every tab that judges it (#2866).
/// </summary>
/// <remarks>
/// The health score took a drive's <see cref="DiskHealthReport.HealthPercent"/>, the life left, as its score, so a
/// Healthy SSD at 41% wear and 45 °C scored 59: the Dashboard said "Disk health critical", the health card asked for a
/// backup, and System Health called the same drive Healthy.
/// </remarks>
public class DiskConditionTests
{
    private static DiskHealthReport Drive(int? wear = null, double? celsius = null, long? readErrors = null,
                                          string status = "Healthy", string name = "Disk") =>
        new() { FriendlyName = name, HealthStatus = status, WearPercent = wear, TemperatureC = celsius, ReadErrors = readErrors };

    private static void ApplyVerdict(DiskHealthReport r) =>
        typeof(DiskHealthService).GetMethod("ApplyVerdict", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [r]);

    [Theory]
    [InlineData(41, 45.0)]   // the drive from the report
    [InlineData(41, 61.0)]   // warm under load
    [InlineData(89, 69.0)]   // just short of both limits
    [InlineData(0, null)]
    [InlineData(null, 50.0)]
    public void AHealthyDrive_WithWearAndWarmthBelowTheLimits_IsFine(int? wear, double? celsius)
    {
        var drive = Drive(wear, celsius);

        Assert.Equal(DiskCondition.Fine, drive.Condition);
        Assert.Equal(100, HealthScoreService.ComputeDiskScore([drive]));
    }

    [Theory]
    [InlineData(90, null, null, "Healthy")]     // worn out
    [InlineData(10, 70.0, null, "Healthy")]     // runs hot
    [InlineData(10, 40.0, 3L, "Healthy")]       // I/O errors
    [InlineData(null, null, null, "Warning")]   // Windows warns
    public void ADriveAtALimit_OrThatWindowsWarnsAbout_IsWatched(int? wear, double? celsius, long? errors, string status)
    {
        var drive = Drive(wear, celsius, errors, status);

        Assert.Equal(DiskCondition.Watch, drive.Condition);
        Assert.Equal(DiskHealthReport.WarningCeiling, HealthScoreService.ComputeDiskScore([drive]));
    }

    [Fact]
    public void ADriveWindowsReportsAsFailing_IsFailing_WhateverItsCountersSay()
    {
        var drive = Drive(wear: 0, celsius: 35, status: "Unhealthy");

        Assert.Equal(DiskCondition.Failing, drive.Condition);
        Assert.Equal(DiskHealthReport.FailingCeiling, HealthScoreService.ComputeDiskScore([drive]));
    }

    [Fact]
    public void ADriveNothingWasReadFrom_IsUnknown_AndTheDiskIsUnavailable()
    {
        var drive = Drive(status: "Unknown");

        Assert.Equal(DiskCondition.Unknown, drive.Condition);
        Assert.Contains(HealthScoreService.DiskComponent,
                        HealthScoreService.Evaluate(null, [drive], null, null, null).UnavailableComponents);
    }

    [Theory]
    [InlineData(41, 45.0, null, "Healthy", DiskCondition.Fine)]
    [InlineData(89, 69.0, null, "Healthy", DiskCondition.Fine)]
    [InlineData(90, 30.0, null, "Healthy", DiskCondition.Watch)]
    [InlineData(5, 70.0, null, "Healthy", DiskCondition.Watch)]
    [InlineData(5, 30.0, 2L, "Healthy", DiskCondition.Watch)]
    [InlineData(5, 30.0, null, "Warning", DiskCondition.Watch)]
    [InlineData(5, 30.0, null, "Unhealthy", DiskCondition.Failing)]
    public void SystemHealthsVerdict_AndTheCondition_GiveTheSameAnswer(
        int? wear, double? celsius, long? errors, string status, DiskCondition expected)
    {
        var drive = Drive(wear, celsius, errors, status);
        ApplyVerdict(drive);

        var colour = expected switch
        {
            DiskCondition.Fine => StatusColors.Good,
            DiskCondition.Watch => StatusColors.Warning,
            _ => StatusColors.Bad,
        };
        Assert.Equal(expected, drive.Condition);
        Assert.Equal(colour, drive.VerdictColorHex);
    }

    [Fact]
    public void TheReportedDrive_GetsNoDashboardAlert_AndNoBackupAdvice()
    {
        // 41% wear and 61 °C on a drive Windows reports Healthy: 59% of its rated endurance left, and nothing to do.
        var drive = Drive(wear: 41, celsius: 61, name: "NVMe SSD");

        var result = HealthScoreService.Evaluate(null, [drive], null, null, null);
        var (title, severity) = DashboardViewModel.ClassifySmartHealth(
            result.DiskScore, result.IsUnavailable(HealthScoreService.DiskComponent));

        Assert.Equal(AlertSeverity.Green, severity);
        Assert.Equal("All SMART indicators healthy", title);
        Assert.DoesNotContain(result.Recommendations, r => r.Message.Contains("consider backup", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBackupAdvice_NamesTheDriveThatNeedsIt_NotTheMostWornHealthyOne()
    {
        // The worst drive used to be the lowest HealthPercent, which is the most worn healthy drive here (59 - 5 = 54)
        // rather than the one running hot (70).
        var worn = Drive(wear: 41, celsius: 55, name: "Worn but healthy");
        var hot = Drive(wear: 0, celsius: 72, name: "Runs hot");

        var result = HealthScoreService.Evaluate(null, [worn, hot], null, null, null);

        var advice = Assert.Single(result.Recommendations, r => r.Message.Contains("consider backup", StringComparison.Ordinal));
        Assert.Equal("Runs hot health degraded — consider backup", advice.Message);
        Assert.Equal("warning", advice.Severity);
    }
}
