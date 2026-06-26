// SysManager · SafetyDatabaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SafetyDatabase"/> — the curated lookup that drives the
/// risk warnings shown before a user disables a Windows service or feature. The
/// fail-safe defaults (unknown service → Critical, unknown feature → Caution) are
/// the load-bearing behavior: a wrong default could let someone disable a core
/// service without a warning.
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
    public void GetServiceSafety_MapsKnownServicesToTier(string service, SafetyLevel expected)
        => Assert.Equal(expected, SafetyDatabase.GetServiceSafety(service).Level);

    [Fact]
    public void GetServiceSafety_UnknownService_DefaultsToCritical()
    {
        // Fail-safe: an unrecognised service must NOT be presented as safe to disable.
        var (level, description) = SafetyDatabase.GetServiceSafety("Totally.Made.Up.Service");
        Assert.Equal(SafetyLevel.Critical, level);
        Assert.Contains("critical", description, StringComparison.OrdinalIgnoreCase);
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
    public void GetFeatureSafety_UnknownFeature_DefaultsToCaution()
    {
        // Features default to Caution (not Critical) — unknown optional features are
        // generally reversible, but still warrant a "check the docs" warning.
        var (level, description) = SafetyDatabase.GetFeatureSafety("Made-Up-Feature");
        Assert.Equal(SafetyLevel.Caution, level);
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Theory]
    [InlineData("telnetclient")]
    [InlineData("TELNETCLIENT")]
    public void GetFeatureSafety_IsCaseInsensitive(string feature)
        => Assert.Equal(SafetyLevel.Safe, SafetyDatabase.GetFeatureSafety(feature).Level);
}
