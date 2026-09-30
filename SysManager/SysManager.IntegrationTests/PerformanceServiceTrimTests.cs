// SysManager · PerformanceServiceTrimTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The real <c>EmptyWorkingSet</c> call behind Trim RAM, made on this test process alone.
/// </summary>
/// <remarks>
/// The unit suite drives the trim loop with a process list of its own. Before #2557 two unit tests ran the real trim over
/// every process on the machine, on every run, because that was the only way to reach the P/Invoke.
/// </remarks>
public class PerformanceServiceTrimTests
{
    [Fact]
    public void TrimWorkingSet_OnThisProcess_Succeeds()
    {
        using var self = Process.GetCurrentProcess();

        Assert.True(PerformanceService.TrimWorkingSet(self));
    }
}
