// SysManager · PerformanceServiceExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the new PerformanceService features: restore point creation,
/// RAM working set trim, and hibernation toggle.
/// Focuses on testable logic; actual system calls are integration-level.
/// </summary>
public class PerformanceServiceExtendedTests
{
    // ── ReadHibernationEnabled ──

    [Fact]
    public void ReadHibernationEnabled_ReturnsBoolean()
    {
        var result = PerformanceService.ReadHibernationEnabled();
        Assert.IsType<bool>(result);
    }

    // ── TrimWorkingSets ──

    [Fact]
    public void TrimWorkingSets_ReturnsNonNegative()
    {
        var count = PerformanceService.TrimWorkingSets();
        Assert.True(count >= 0, $"Expected non-negative, got {count}");
    }

    [Fact]
    public void TrimWorkingSets_TrimsSomeProcesses()
    {
        var count = PerformanceService.TrimWorkingSets();
        Assert.True(count > 0, "Expected at least 1 process trimmed");
    }

    // ── Service construction ──

    [Fact]
    public void Service_AcceptsPowerShellRunner()
    {
        var ps = new PowerShellRunner();
        var service = new PerformanceService(ps);
        Assert.NotNull(service);
    }
}
