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

    [Fact]
    public async Task GetModules_Completes_And_ReturnsList()
    {
        var svc = new MemoryTestService();
        var modules = await svc.GetModulesAsync();
        Assert.NotNull(modules);
        // On a real host there is at least one module with a positive capacity.
        foreach (var m in modules)
        {
            Assert.True(m.CapacityGB > 0, $"Module {m.Slot} has non-positive capacity");
        }
    }
}
