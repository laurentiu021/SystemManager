// SysManager · BatteryHealthViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.CompilerServices;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BatteryHealthViewModel"/>. Verifies initial state, command availability, a failed battery
/// read, and the capacity history card.
/// </summary>
public class BatteryHealthViewModelTests
{
    /// <summary>A report service that never has a history to give, so no test here runs <c>powercfg</c>.</summary>
    private static IBatteryReportService NoReport()
    {
        var report = Substitute.For<IBatteryReportService>();
        report.ReadHistoryAsync(Arg.Any<CancellationToken>()).Returns(BatteryReportRead.Failed);
        return report;
    }

    [Fact]
    public void Constructor_RefreshCommand_Exists()
    {
        var vm = new BatteryHealthViewModel(new Services.BatteryService(), NoReport());
        Assert.NotNull(vm.RefreshCommand);
    }

    [Fact]
    public void Constructor_Battery_NotNull()
    {
        var vm = new BatteryHealthViewModel(new Services.BatteryService(), NoReport());
        Assert.NotNull(vm.Battery);
    }

    [Fact]
    public void Summary_HasDefaultValue()
    {
        var vm = new BatteryHealthViewModel(new Services.BatteryService(), NoReport());
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    [Fact]
    public void Battery_CanBeReplaced()
    {
        var vm = new BatteryHealthViewModel(new Services.BatteryService(), NoReport());
        var changed = vm.RecordPropertyChanges();

        vm.Battery = new Models.BatteryInfo { Name = "Test" };
        Assert.Contains("Battery", changed);
        Assert.Equal("Test", vm.Battery.Name);
    }

    [Fact]
    public void Summary_CanBeChanged()
    {
        // Asserts the NOTIFICATION, not just the round-trip. A set/get pair passes on a plain auto-
        // property, so it would stay green if [ObservableProperty] were dropped from Summary — and a
        // bound label silently freezing is the only regression this property realistically has.
        // Battery_CanBeReplaced two tests above already uses this pattern.
        var vm = new BatteryHealthViewModel(new Services.BatteryService(), NoReport());
        var changed = vm.RecordPropertyChanges();

        vm.Summary = "Custom summary";

        Assert.Contains("Summary", changed);
        Assert.Equal("Custom summary", vm.Summary);
    }

    // ── A read that failed is not "no battery" (#2503) ──
    //
    // The service reported a failed read as no battery, so a laptop was told "No battery detected — this device
    // runs on AC power only." The view model's own "Could not read battery information." was never reached.

    private static readonly Models.BatteryInfo Laptop = new()
    {
        HasBattery = true,
        Name = "Primary",
        ChargePercent = 80,
        Status = "Charging",
    };

    private static async Task<BatteryHealthViewModel> SettledVm(
        Func<Task<Models.BatteryInfo?>> read, Func<CancellationToken, Task<BatteryReportRead>>? readHistory = null)
    {
        var vm = new BatteryHealthViewModel(read, readHistory ?? (_ => Task.FromResult(BatteryReportRead.Failed)));
        await vm.InitializationComplete;
        return vm;
    }

    [Fact]
    public async Task AReadThatFailed_SaysSo_NotThatThereIsNoBattery()
    {
        var vm = await SettledVm(() => Task.FromResult<Models.BatteryInfo?>(null));

        Assert.True(vm.ReadFailed);
        Assert.Equal("Could not read the battery. Press Refresh to try again.", vm.Summary);
        Assert.Equal("The battery could not be read.", vm.StatusMessage);
        Assert.StartsWith("The battery could not be read", vm.NoBatteryText);
        Assert.DoesNotContain("AC power", vm.Summary);
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheLastReading_AndSaysItIsFromThen()
    {
        var reads = 0;
        var vm = await SettledVm(() => Task.FromResult(++reads == 1 ? Laptop : null));
        var summary = vm.Summary;
        Assert.True(vm.Battery.HasBattery);   // the premise: the first read showed the battery

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Same(Laptop, vm.Battery);
        Assert.Equal(summary, vm.Summary);
        Assert.Contains("from the last reading", vm.StatusMessage);
    }

    [Fact]
    public async Task NoBattery_IsStillReportedAsNoBattery()
    {
        var vm = await SettledVm(() => Task.FromResult<Models.BatteryInfo?>(new Models.BatteryInfo()));

        Assert.False(vm.ReadFailed);
        Assert.Equal("No battery detected on this device.", vm.NoBatteryText);
        Assert.Equal("No battery found.", vm.StatusMessage);
    }

    [Fact]
    public async Task AReadThatWorksAfterAFailedOne_ClearsTheFailure()
    {
        var reads = 0;
        var vm = await SettledVm(() => Task.FromResult(++reads == 1 ? null : Laptop));
        Assert.True(vm.ReadFailed);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.ReadFailed);
        Assert.Same(Laptop, vm.Battery);
        Assert.Equal("Battery data loaded.", vm.StatusMessage);
    }

    // ── The capacity history (#1513) ──

    private static readonly DateTime FirstWeek = new(2026, 4, 6);

    private static BatteryReportRead History(params (int Day, double Percent)[] points) => new(
        BatteryReportOutcome.Read,
        [.. points.Select(p => new Models.BatteryCapacityPoint(FirstWeek.AddDays(p.Day), 50000, (long)Math.Round(p.Percent * 500)))]);

    private static readonly BatteryReportRead SixMonths = History((0, 100), (91, 98), (182, 96));

    private static readonly BatteryReportRead NoHistory = new(BatteryReportOutcome.NoHistory, []);

    /// <summary>History reads answered in order, the last one repeating; <paramref name="calls"/> counts them.</summary>
    private static Func<CancellationToken, Task<BatteryReportRead>> Answers(StrongBox<int> calls, params BatteryReportRead[] reads)
        => _ =>
        {
            var answer = reads[Math.Min(calls.Value, reads.Length - 1)];
            calls.Value++;
            return Task.FromResult(answer);
        };

    private static Func<Task<Models.BatteryInfo?>> Always(Models.BatteryInfo? battery) => () => Task.FromResult(battery);

    [Fact]
    public async Task SixMonthsOfHistory_ShowTheVerdictAndTheLine()
    {
        var vm = await SettledVm(Always(Laptop), Answers(new StrongBox<int>(), SixMonths));

        Assert.Equal("Normal wear", vm.WearVerdict?.Headline);
        Assert.True(vm.CapacityChart.HasChart);
        Assert.Equal("", vm.HistoryTooShort);
        Assert.Equal("", vm.HistoryNote);
    }

    [Fact]
    public async Task AShortHistory_SaysHowMuchThereIsSoFar_AndDrawsNothing()
    {
        var vm = await SettledVm(Always(Laptop), Answers(new StrongBox<int>(), History((0, 100), (14, 99))));

        Assert.Null(vm.WearVerdict);
        Assert.False(vm.CapacityChart.HasChart);
        Assert.Equal("Windows has recorded 2 weeks so far. The trend appears after about a month.", vm.HistoryTooShort);
        Assert.Equal("", vm.HistoryNote);
    }

    [Fact]
    public async Task AHistoryWindowsDoesNotKeep_IsSaidInOneLine()
    {
        var vm = await SettledVm(Always(Laptop), Answers(new StrongBox<int>(), NoHistory));

        Assert.Equal(BatteryHealthViewModel.NoHistoryKept, vm.HistoryNote);
        Assert.Null(vm.WearVerdict);
        Assert.False(vm.CapacityChart.HasChart);
        Assert.Equal("", vm.HistoryTooShort);
    }

    [Fact]
    public async Task AReportThatCouldNotBeRead_IsSaidInOneLine_NotAsNoHistory()
    {
        var vm = await SettledVm(Always(Laptop), Answers(new StrongBox<int>(), BatteryReportRead.Failed));

        Assert.Equal(BatteryHealthViewModel.HistoryUnreadable, vm.HistoryNote);
        Assert.Null(vm.WearVerdict);
        Assert.False(vm.CapacityChart.HasChart);
    }

    [Fact]
    public async Task AFailedReadAfterAGoodOne_KeepsWhatTheGoodOneShowed()
    {
        // The history only grows, so what is on the card is still true; a failure would only take it away.
        var vm = await SettledVm(Always(Laptop), Answers(new StrongBox<int>(), SixMonths, BatteryReportRead.Failed));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Normal wear", vm.WearVerdict?.Headline);
        Assert.True(vm.CapacityChart.HasChart);
        Assert.Equal("", vm.HistoryNote);
    }

    [Fact]
    public async Task AGoodReadAfterAFailedOne_ReplacesTheNote()
    {
        var vm = await SettledVm(Always(Laptop), Answers(new StrongBox<int>(), BatteryReportRead.Failed, SixMonths));
        Assert.Equal(BatteryHealthViewModel.HistoryUnreadable, vm.HistoryNote);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("", vm.HistoryNote);
        Assert.True(vm.CapacityChart.HasChart);
    }

    [Fact]
    public async Task AHistoryThatGrowsPastAMonth_ReplacesTheShortMessageWithTheVerdict()
    {
        var vm = await SettledVm(Always(Laptop), Answers(new StrongBox<int>(), History((0, 100), (14, 99)), SixMonths));
        Assert.NotEqual("", vm.HistoryTooShort);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("", vm.HistoryTooShort);
        Assert.NotNull(vm.WearVerdict);
        Assert.True(vm.CapacityChart.HasChart);
    }

    [Fact]
    public async Task Refresh_ReadsTheHistoryAgain()
    {
        var calls = new StrongBox<int>();
        var vm = await SettledVm(Always(Laptop), Answers(calls, SixMonths));
        Assert.Equal(1, calls.Value);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, calls.Value);
    }

    [Fact]
    public async Task WithNoBattery_TheHistoryIsNeverRead()
    {
        // The card is not shown without a battery, and the report would be a process and a file for nothing.
        var calls = new StrongBox<int>();
        var vm = await SettledVm(Always(new Models.BatteryInfo()), Answers(calls, SixMonths));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, calls.Value);
        Assert.Equal("", vm.HistoryNote);
    }

    [Fact]
    public async Task ABatteryThatCouldNotBeRead_AndWasNeverShown_HasNoHistoryRead()
    {
        var calls = new StrongBox<int>();
        await SettledVm(Always(null), Answers(calls, SixMonths));

        Assert.Equal(0, calls.Value);
    }

    [Fact]
    public async Task AFailedBatteryRead_StillRefreshesTheHistoryOfTheBatteryShown()
    {
        // The figures on screen are from the last reading, and so is the card; both are refreshed when they can be.
        var reads = 0;
        var calls = new StrongBox<int>();
        var vm = await SettledVm(() => Task.FromResult(++reads == 1 ? Laptop : null), Answers(calls, SixMonths));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.ReadFailed);
        Assert.Equal(2, calls.Value);
    }

    [Fact]
    public async Task WhileTheFirstReadRuns_TheCardSaysSo_AndARefreshKeepsTheChartMeanwhile()
    {
        var pending = new TaskCompletionSource<BatteryReportRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var vm = new BatteryHealthViewModel(Always(Laptop), _ => ++calls == 1 ? pending.Task : Task.FromResult(SixMonths));

        Assert.Equal(BatteryHealthViewModel.HistoryReading, vm.HistoryNote);
        Assert.True(vm.IsBusy);
        Assert.False(vm.RefreshCommand.CanExecute(null));   // one read at a time

        pending.SetResult(SixMonths);
        await vm.InitializationComplete;
        Assert.Equal("", vm.HistoryNote);

        // A second read starts with the chart already up: it stays, and "reading" is not put back over it.
        var changes = vm.RecordPropertyChanges();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.DoesNotContain(nameof(vm.HistoryNote), changes);
        Assert.True(vm.CapacityChart.HasChart);
    }

    [Fact]
    public async Task ClosingTheTab_WhileTheReportIsWritten_StopsWaitingForIt()
    {
        CancellationToken given = default;
        var vm = new BatteryHealthViewModel(Always(Laptop), ct =>
        {
            given = ct;
            var stalled = new TaskCompletionSource<BatteryReportRead>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => stalled.TrySetCanceled(ct));
            return stalled.Task;
        });

        vm.Dispose();
        await vm.InitializationComplete.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(given.IsCancellationRequested);
        Assert.Null(vm.InitializationFault);
        Assert.False(vm.CapacityChart.HasChart);
    }
}
