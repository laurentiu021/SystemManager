// SysManager · PerformancePlanTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The power plans on the PC, read through the power API and through powercfg on the PC running the tests, which are
/// only read.
/// </summary>
/// <remarks>
/// A restore leaves out a recorded plan that is not among them (#2716), so a list that missed a plan would leave a
/// real one out.
/// </remarks>
public partial class PerformancePlanTests
{
    [Fact]
    public async Task ThePlansOnThePc_AreTheOnesPowercfgLists()
    {
        var plans = PerformanceService.PlansOnThePc();
        Assert.NotNull(plans);

        using var runner = new PowerShellRunner();
        ConcurrentQueue<string> lines = new();
        runner.LineReceived += line => lines.Enqueue(line.Text);
        var exitCode = await runner.RunProcessAsync("powercfg.exe", "/list", CancellationToken.None, PowerShellRunner.OemEncoding);
        Assert.Equal(0, exitCode);

        var listed = lines.SelectMany(l => PlanGuid().Matches(l).Select(m => Guid.Parse(m.Value))).ToHashSet();
        Assert.NotEmpty(listed);
        Assert.True(listed.SetEquals(plans),
            $"powercfg lists {string.Join(", ", listed)}; the power API {string.Join(", ", plans)}");
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex PlanGuid();
}
