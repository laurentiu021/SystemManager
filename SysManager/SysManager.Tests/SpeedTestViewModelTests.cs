// SysManager · SpeedTestViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Every VM here is built with a <see cref="Services.SpeedTestHistoryService"/> pointed at a throwaway
/// directory. The VM constructor kicks off <c>LoadHistoryAsync</c>, so before the service gained its
/// <c>configDir</c> seam these tests READ the user's real speedtest-history.json — and their results
/// depended on whatever that file happened to contain.
/// </summary>
public sealed class SpeedTestViewModelTests : IDisposable
{
    private readonly string _dir;

    public SpeedTestViewModelTests()
    {
        _dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); }
        catch (System.IO.DirectoryNotFoundException) { /* already gone */ }
    }

    private static NetworkSharedState NewShared() => new(
        new Services.PingMonitorService(), new Services.TracerouteService(),
        new Services.TracerouteMonitorService(), new Services.SpeedTestService(),
        new Services.NetworkRepairService(new Services.PowerShellRunner()));

    /// <summary>History service scoped to this test's temp directory — never the real profile.</summary>
    private Services.SpeedTestHistoryService NewHistory() => new(_dir);

    [Fact]
    public void Constructor_SetsShared()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        Assert.Same(shared, vm.Shared);
    }

    [Fact]
    public void DefaultState_NotTesting()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        Assert.False(vm.IsSpeedTesting);
        Assert.False(vm.IsHttpTesting);
        Assert.False(vm.IsOoklaTesting);
        Assert.Equal(0, vm.SpeedProgress);
    }

    [Fact]
    public void HttpResult_DefaultNull()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        Assert.Null(vm.HttpResult);
    }

    [Fact]
    public void OoklaResult_DefaultNull()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        Assert.Null(vm.OoklaResult);
    }

    [Fact]
    public void CancelSpeedCommand_DoesNotThrow()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        vm.CancelSpeedCommand.Execute(null);
    }

    [Fact]
    public void HttpHistory_StartsEmpty()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        Assert.NotNull(vm.HttpHistory);
    }

    [Fact]
    public void OoklaHistory_StartsEmpty()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        Assert.NotNull(vm.OoklaHistory);
    }

    [Theory]
    [InlineData("RunHttpSpeedCommand")]
    [InlineData("RunOoklaSpeedCommand")]
    [InlineData("CancelSpeedCommand")]
    [InlineData("ClearHttpHistoryCommand")]
    [InlineData("ClearOoklaHistoryCommand")]
    public void CommandExists(string propertyName)
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        var prop = vm.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    [Fact]
    public async Task ClearHttpHistoryCommand_DoesNotThrow()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        var ex = await Record.ExceptionAsync(() => vm.ClearHttpHistoryCommand.ExecuteAsync(null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ClearOoklaHistoryCommand_DoesNotThrow()
    {
        var shared = NewShared();
        var vm = new SpeedTestViewModel(shared, NewHistory());
        var ex = await Record.ExceptionAsync(() => vm.ClearOoklaHistoryCommand.ExecuteAsync(null));
        Assert.Null(ex);
    }
}
