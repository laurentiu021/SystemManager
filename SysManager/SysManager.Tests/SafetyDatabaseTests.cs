// SysManager · SafetyDatabaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SafetyDatabase"/> — the curated lookup that drives the
/// risk warnings shown before a user disables a Windows service or feature. The
/// fail-safe default (a service or feature not in the list is Not rated, which Services
/// refuses to stop or disable) is the load-bearing behavior: a wrong default could let
/// someone disable a core service without a warning.
/// </summary>
public class SafetyDatabaseTests
{
    // ---------- services ----------

    [Theory]
    [InlineData("DiagTrack", SafetyLevel.Safe)]        // telemetry — safe to disable
    [InlineData("SysMain", SafetyLevel.Safe)]
    [InlineData("RemoteRegistry", SafetyLevel.Safe)]
    [InlineData("wuauserv", SafetyLevel.Caution)]      // Windows Update — caution
    [InlineData("Spooler", SafetyLevel.Caution)]
    [InlineData("AudioSrv", SafetyLevel.Caution)]
    [InlineData("RpcSs", SafetyLevel.Critical)]        // core IPC — critical
    [InlineData("lsass", SafetyLevel.Critical)]
    [InlineData("WinDefend", SafetyLevel.Critical)]
    [InlineData("lmhosts", SafetyLevel.Safe)]          // the gaming advice says to turn these two off (#2611)
    [InlineData("WbioSrvc", SafetyLevel.Caution)]
    public void GetServiceSafety_MapsKnownServicesToTier(string service, SafetyLevel expected)
        => Assert.Equal(expected, SafetyDatabase.GetServiceSafety(service).Level);

    [Fact]
    public void GetServiceSafety_UnknownService_IsNotRated_AndSaysWhoDoesNotKnow()
    {
        // Fail-safe: an unrecognised service must NOT be presented as safe to disable. Nor as critical any more:
        // that put the red pill on most rows and taught the user to ignore it (#1512).
        var (level, description) = SafetyDatabase.GetServiceSafety("Totally.Made.Up.Service");

        Assert.Equal(SafetyLevel.NotRated, level);
        Assert.False(new ServiceEntry { SafetyLevel = level }.MayBeTurnedOff);
        Assert.StartsWith("SysManager has not rated this service.", description, StringComparison.Ordinal);
        Assert.Contains($"one of the {SafetyDatabase.RatedServiceCount} services", description, StringComparison.Ordinal);
        Assert.DoesNotContain("critical", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryServiceTheGamingAdviceSaysToTurnOff_IsOneDisableActsOn()
    {
        // The advice said WbioSrvc and lmhosts were safe to disable, and with no rating Disable refused them (#2611).
        var offenders = ServiceManagerService.GamingGuide
            .Where(g => g.Value.Rec is "safe-to-disable" or "advanced")
            .Where(g => !new ServiceEntry { SafetyLevel = SafetyDatabase.GetServiceSafety(g.Key).Level }.MayBeTurnedOff)
            .Select(g => g.Key)
            .ToList();

        Assert.True(ServiceManagerService.GamingGuide.Count(g => g.Value.Rec == "safe-to-disable") >= 10,
            "the advice list was not read, so nothing below is checked");
        Assert.True(offenders.Count == 0,
            "The gaming advice tells the user to turn these off, and Disable refuses them: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("diagtrack")]   // lower
    [InlineData("DIAGTRACK")]   // upper
    [InlineData("DiagTrack")]   // exact
    public void GetServiceSafety_IsCaseInsensitive(string service)
        => Assert.Equal(SafetyLevel.Safe, SafetyDatabase.GetServiceSafety(service).Level);

    [Fact]
    public void GetServiceSafety_KnownService_ReturnsNonEmptyDescription()
        => Assert.False(string.IsNullOrWhiteSpace(SafetyDatabase.GetServiceSafety("wuauserv").Description));

    [Theory]
    [InlineData("AudioSrv")]
    [InlineData("Audiosrv")]
    [InlineData("audiosrv")]
    public void GetServiceSafety_AudioService_KeepsRicherDescription(string service)
    {
        // Regression (idx 173/308): the case-insensitive dictionary previously held both
        // "AudioSrv" and "Audiosrv"; the second silently overwrote the first, dropping the
        // "Only disable on headless servers" guidance. The duplicate was removed, so the
        // richer description must survive for every casing.
        var (_, description) = SafetyDatabase.GetServiceSafety(service);
        Assert.Contains("headless servers", description, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- features ----------

    [Theory]
    [InlineData("TelnetClient", SafetyLevel.Safe)]
    [InlineData("SMB1Protocol", SafetyLevel.Safe)]
    [InlineData("NetFx3", SafetyLevel.Caution)]
    [InlineData("Printing-Foundation-Features", SafetyLevel.Caution)]
    [InlineData("Microsoft-Hyper-V-All", SafetyLevel.Critical)]
    [InlineData("Containers", SafetyLevel.Critical)]
    public void GetFeatureSafety_MapsKnownFeaturesToTier(string feature, SafetyLevel expected)
        => Assert.Equal(expected, SafetyDatabase.GetFeatureSafety(feature).Level);

    [Fact]
    public void GetFeatureSafety_UnknownFeature_IsNotRated_AndSaysToLookItUp()
    {
        // It read Caution, which claimed a rating SysManager does not have (#1512). Toggling it is still offered, so the
        // words say to look it up rather than that it is left alone.
        var (level, description) = SafetyDatabase.GetFeatureSafety("Made-Up-Feature");

        Assert.Equal(SafetyLevel.NotRated, level);
        Assert.StartsWith("SysManager has not rated this feature.", description, StringComparison.Ordinal);
        Assert.Contains($"one of the {SafetyDatabase.RatedFeatureCount} features", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("telnetclient")]
    [InlineData("TELNETCLIENT")]
    public void GetFeatureSafety_IsCaseInsensitive(string feature)
        => Assert.Equal(SafetyLevel.Safe, SafetyDatabase.GetFeatureSafety(feature).Level);
}
