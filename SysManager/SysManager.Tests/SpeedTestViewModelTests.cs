// SysManager · SpeedTestViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Every VM here is built with a <see cref="Services.SpeedTestHistoryService"/> pointed at a throwaway
/// directory. The VM constructor kicks off <c>LoadHistoryAsync</c>, so before the service gained its
/// <c>configDir</c> seam these tests READ the user's real speedtest-history.json — and their results
/// depended on whatever that file happened to contain.
/// </summary>
// Serialized: the view model's speed tests take the process-wide Network lock, and these tests run its commands. See
// ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection for why any command counts.
[Collection("ProcessWideStatics")]
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

    // ---------- saved results that cannot be read (#2521) ----------

    [Fact]
    public async Task ALoad_ThatCannotReadTheSavedResults_SaysSoUnderBothCards()
    {
        // A history that could not be read used to show as no results, and the next run was saved over it.
        var history = NewHistory();
        Assert.True(await history.SaveAsync(At("HTTP", 1)));

        SpeedTestViewModel vm;
        // Held open with delete sharing only, so the first load cannot read it.
        using (new System.IO.FileStream(System.IO.Path.Combine(_dir, "speedtest-history.json"),
                   System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Delete))
        {
            vm = new SpeedTestViewModel(NewShared(), history);
            await vm.InitializationComplete;
        }

        Assert.Equal(SpeedTestViewModel.HistoryUnreadable, vm.HttpStatus);
        Assert.Equal(SpeedTestViewModel.HistoryUnreadable, vm.OoklaStatus);
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

    // ---------- a run whose upload or ping was not measured (#2504) ----------
    //
    // A ping with no answer used to read 0 ms, a perfect score, on the card and in the history.

    private static Services.ISpeedTestService EngineReturning(Models.SpeedTestResult result)
    {
        var engine = Substitute.For<Services.ISpeedTestService>();
        engine.RunHttpAsync(Arg.Any<IProgress<(int Percent, string Message)>?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(result));
        engine.RunOoklaAsync(Arg.Any<IProgress<(int Percent, string Message)>?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
              .Returns(Task.FromResult(result));
        return engine;
    }

    [Fact]
    public async Task AnHttpRunWhosePingGotNoAnswer_ShowsNoPing_SaysWhy_AndSavesNoPing()
    {
        var history = NewHistory();
        var noPing = new Models.SpeedTestResult("HTTP", 312.4, 41.7, null, "speed.cloudflare.com", new DateTime(2026, 9, 29, 9, 0, 0));
        var vm = new SpeedTestViewModel(NewShared(), history, EngineReturning(noPing));
        await vm.InitializationComplete;

        await vm.RunHttpSpeedCommand.ExecuteAsync(null);

        Assert.NotNull(vm.HttpResult);
        Assert.Equal("—", vm.HttpResult.PingDisplay);
        Assert.Equal("HTTP done — no reply to ping (some networks block it)", vm.HttpStatus);
        Assert.Null(Assert.Single(vm.HttpHistory).PingMs);
        var saved = await history.LoadAsync();
        Assert.NotNull(saved);
        Assert.Null(Assert.Single(saved).PingMs);
    }

    [Fact]
    public async Task AnOoklaRun_ThatMeasuredEverything_SaysOnlyThatItIsDone()
    {
        var history = NewHistory();
        var measured = new Models.SpeedTestResult("Ookla", 480.2, 95.1, 7.4, "Bucharest", new DateTime(2026, 9, 29, 9, 5, 0));
        var vm = new SpeedTestViewModel(NewShared(), history, EngineReturning(measured));
        await vm.InitializationComplete;

        await vm.RunOoklaSpeedCommand.ExecuteAsync(null);

        Assert.Equal("Ookla done", vm.OoklaStatus);
        Assert.Equal("7 ms", vm.OoklaResult?.PingDisplay);
        var saved = await history.LoadAsync();
        Assert.NotNull(saved);
        Assert.Equal(measured, Assert.Single(saved));
    }

    [Theory]
    [InlineData(41.7, 12.3, true, "HTTP done")]
    [InlineData(41.7, 12.3, false, "HTTP done — result could not be saved to history")]
    [InlineData(41.7, null, true, "HTTP done — no reply to ping (some networks block it)")]
    [InlineData(null, 12.3, true, "HTTP done — upload could not be measured")]
    [InlineData(null, null, false,
        "HTTP done — upload could not be measured; no reply to ping (some networks block it); result could not be saved to history")]
    public void DescribeFinished_SaysWhatWasNotMeasured_AndWhetherItWasSaved(double? up, double? ping, bool saved, string expected)
        => Assert.Equal(expected, SpeedTestViewModel.DescribeFinished(
            "HTTP", new Models.SpeedTestResult("HTTP", 100, up, ping, "server", new DateTime(2026, 9, 29)), saved));
}
