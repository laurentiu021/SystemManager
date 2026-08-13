// SysManager · MemoryTestServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class MemoryTestServiceTests
{
    [Fact]
    public async Task CheckErrorLogs_Completes()
    {
        var svc = new MemoryTestService();
        var summary = await svc.CheckErrorLogsAsync();
        Assert.NotNull(summary);
        Assert.True(summary.WheaMemoryErrors >= 0);
        Assert.True(summary.MemoryDiagnosticResults >= 0);
    }

    [Fact]
    public async Task CheckErrorLogs_Cancellation_IsSafe()
    {
        var svc = new MemoryTestService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Record.ExceptionAsync(async () => await svc.CheckErrorLogsAsync(cts.Token));
        Assert.True(ex == null || ex is OperationCanceledException);
    }

    /// <summary>
    /// The module inventory now comes from <see cref="SystemInfoService"/> — the cached path the
    /// System Health tab actually binds. <c>MemoryTestService.GetModulesAsync</c> used to return a
    /// near-duplicate <c>MemoryModuleHealth</c> record that only this test ever reached, so the two
    /// models drifted: the unreachable one carried the slot and the configured speed, while the one
    /// on screen did not. This test follows the surviving path.
    /// </summary>
    [Fact]
    public async Task MemoryModules_AreEnumeratedWithAPositiveCapacity()
    {
        var snapshot = await new SystemInfoService().CaptureAsync();

        Assert.NotNull(snapshot.Memory.Modules);
        foreach (var m in snapshot.Memory.Modules)
        {
            Assert.True(m.CapacityGB > 0, $"Module {m.Slot} has non-positive capacity");

            // ConfiguredSpeedMHz is 0 when Windows does not report it, but it must never exceed the
            // rated speed — that would mean the two WMI fields were read into the wrong properties.
            if (m.ConfiguredSpeedMHz > 0 && m.SpeedMHz > 0)
                Assert.True(m.ConfiguredSpeedMHz <= m.SpeedMHz,
                    $"Module {m.Slot} reports running at {m.ConfiguredSpeedMHz} MHz, above its rated "
                    + $"{m.SpeedMHz} MHz — the WMI fields are probably swapped.");
        }
    }
}
