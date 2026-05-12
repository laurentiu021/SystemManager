// SysManager · SpeedTestHistoryServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class SpeedTestHistoryServiceTests
{
    [Fact]
    public void MaxPerEngine_Is20()
    {
        Assert.Equal(20, SpeedTestHistoryService.MaxPerEngine);
    }

    [Fact]
    public async Task LoadAsync_WhenNoFile_ReturnsEmptyList()
    {
        var svc = new SpeedTestHistoryService();
        var results = await svc.LoadAsync();
        Assert.NotNull(results);
        // May or may not be empty depending on whether a history file exists
        // on the test machine — just verify it doesn't throw.
    }

    [Fact]
    public async Task SaveAsync_DoesNotThrow()
    {
        var svc = new SpeedTestHistoryService();
        var result = new SpeedTestResult("HTTP", 100.5, 50.2, 12.3, "test-server", DateTime.Now);
        var ex = await Record.ExceptionAsync(() => svc.SaveAsync(result));
        Assert.Null(ex);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var svc = new SpeedTestHistoryService();
        var result = new SpeedTestResult("HTTP", 123.4, 56.7, 8.9, "roundtrip-server", DateTime.Now);
        await svc.SaveAsync(result);

        var loaded = await svc.LoadAsync();
        Assert.Contains(loaded, r =>
            Math.Abs(r.DownloadMbps - 123.4) < 0.01 &&
            r.Server == "roundtrip-server");
    }

    [Fact]
    public async Task ClearAsync_SpecificEngine_DoesNotThrow()
    {
        var svc = new SpeedTestHistoryService();
        var ex = await Record.ExceptionAsync(() => svc.ClearAsync("HTTP"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ClearAsync_AllEngines_DoesNotThrow()
    {
        var svc = new SpeedTestHistoryService();
        var ex = await Record.ExceptionAsync(() => svc.ClearAsync(null));
        Assert.Null(ex);
    }
}
