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

    // ---------- results saved elsewhere reach the list on screen ----------

    private static Models.SpeedTestResult At(string engine, int minute)
        => new(engine, 100 + minute, 20, 10, "server", new DateTime(2026, 9, 25, 10, minute, 0));

    [Fact]
    public async Task AResultSavedElsewhere_JoinsTheListOnce_EvenWhenThisTabAddsItToo()
    {
        // A run made on this tab arrives twice: once through the history's Saved event, once through the tab's own
        // insert. The order between the two is not fixed, so the second must be a no-op.
        var history = NewHistory();
        var vm = new SpeedTestViewModel(NewShared(), history);
        await vm.InitializationComplete;
        var result = At("HTTP", 1);

        Assert.True(await history.SaveAsync(result));
        vm.AddToHistory(result);

        Assert.Equal(result, Assert.Single(vm.HttpHistory));
        Assert.Empty(vm.OoklaHistory);
    }

    [Fact]
    public async Task AnOoklaResultSavedElsewhere_GoesToTheOoklaList_NewestFirst()
    {
        var history = NewHistory();
        var vm = new SpeedTestViewModel(NewShared(), history);
        await vm.InitializationComplete;

        await history.SaveAsync(At("Ookla", 1));
        await history.SaveAsync(At("Ookla", 2));

        Assert.Equal(new[] { At("Ookla", 2), At("Ookla", 1) }, vm.OoklaHistory);
        Assert.Empty(vm.HttpHistory);
    }

    [Fact]
    public async Task TheListKeepsNoMoreThanTheHistoryFileDoes()
    {
        var vm = new SpeedTestViewModel(NewShared(), NewHistory());
        await vm.InitializationComplete;

        for (var minute = 0; minute <= Services.SpeedTestHistoryService.MaxPerEngine; minute++)
            vm.AddToHistory(At("HTTP", minute));

        Assert.Equal(Services.SpeedTestHistoryService.MaxPerEngine, vm.HttpHistory.Count);
        Assert.Equal(At("HTTP", Services.SpeedTestHistoryService.MaxPerEngine), vm.HttpHistory[0]);
    }

    [Fact]
    public async Task ADisposedTab_NoLongerListens()
    {
        var history = NewHistory();
        var vm = new SpeedTestViewModel(NewShared(), history);
        await vm.InitializationComplete;

        vm.Dispose();
        await history.SaveAsync(At("HTTP", 1));

        Assert.Empty(vm.HttpHistory);
    }

    // ---------- the first load and the Saved event, in the order that loses a row ----------

    [Fact]
    public async Task ALoad_KeepsAResultTheSavedEventHasAlreadyShown()
    {
        // The tab listens before its first load reads the file, so a result saved in between is on screen and
        // missing from what was read. Here it is on screen and in no file at all.
        var vm = new SpeedTestViewModel(NewShared(), NewHistory());
        await vm.InitializationComplete;
        var shown = At("HTTP", 1);
        vm.AddToHistory(shown);

        await vm.LoadHistoryAsync();

        Assert.Equal(shown, Assert.Single(vm.HttpHistory));
    }

    [Fact]
    public async Task ALoad_ShowsAResultOnScreenAndOnDiskOnce()
    {
        // The copy read back from the file is a different object from the one on screen. They must still compare
        // equal after the JSON round trip, or every such result would be listed twice.
        var history = NewHistory();
        var vm = new SpeedTestViewModel(NewShared(), history);
        await vm.InitializationComplete;
        Assert.True(await history.SaveAsync(At("HTTP", 1)));

        await vm.LoadHistoryAsync();

        Assert.Equal(At("HTTP", 1), Assert.Single(vm.HttpHistory));
    }

    [Fact]
    public async Task ALoad_KeepsNoMoreThanTheHistoryFileDoes_WithAResultOnlyOnScreen()
    {
        var history = NewHistory();
        for (var minute = 0; minute < Services.SpeedTestHistoryService.MaxPerEngine; minute++)
            Assert.True(await history.SaveAsync(At("HTTP", minute)));
        var vm = new SpeedTestViewModel(NewShared(), history);
        await vm.InitializationComplete;
        var newest = At("HTTP", Services.SpeedTestHistoryService.MaxPerEngine);
        vm.AddToHistory(newest);

        await vm.LoadHistoryAsync();

        Assert.Equal(Services.SpeedTestHistoryService.MaxPerEngine, vm.HttpHistory.Count);
        Assert.Equal(newest, vm.HttpHistory[0]);
        Assert.DoesNotContain(At("HTTP", 0), vm.HttpHistory);
    }
}
