// SysManager · DarkModeViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using CommunityToolkit.Mvvm.Input;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DarkModeViewModel"/>. The deterministic-surface tests drive the
/// real <see cref="WindowsThemeService"/> on its read-only surface (<c>GetCurrentTheme</c>
/// / <c>LoadSchedule</c>); the mutating-path tests substitute
/// <see cref="IWindowsThemeService"/> so the <c>SwitchToDark</c> / <c>SwitchToLight</c>
/// commands and the schedule-enabling persistence can be executed (asserting
/// <c>SetTheme</c> / <c>SaveSchedule</c> are called with the correct arguments) without
/// writing HKCU or flipping the real Windows theme. The pure schedule predicate
/// <c>WindowsThemeService.ShouldBeDark</c> is already covered by
/// <see cref="WindowsThemeServiceTests"/> and is not duplicated.
///
/// With no <c>Application.Current</c> in the test host, the constructor skips the
/// DispatcherTimer/immediate-evaluate block, so building the VM does not apply the schedule.
/// </summary>
public class DarkModeViewModelTests
{
    // A TEMP configDir, so constructing the service never reads (or writes) the user's real
    // %AppData%\SysManager\darkmode-schedule.json. The registry reads below are read-only and stay.
    private static WindowsThemeService NewRealService() =>
        new(Path.Combine(Path.GetTempPath(), "SysManagerDarkModeTests", Guid.NewGuid().ToString("N")));

    private static DarkModeViewModel NewVm() => NewVm(NewRealService());

    // Settled, as the constructor's first look at the schedule can switch the theme on a worker thread and then write
    // the status line. That look runs only with an Application, which the test host has none of.
    private static DarkModeViewModel NewVm(IWindowsThemeService service)
    {
        var vm = new DarkModeViewModel(service);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    // A substitute that loads a known schedule (so LoadFromSchedule has a non-null result)
    // and reports the Windows theme as light by default. SetTheme and SaveSchedule succeed unless overridden.
    private static IWindowsThemeService FakeThemeService(DarkModeSchedule? schedule = null)
    {
        var service = Substitute.For<IWindowsThemeService>();
        service.LoadSchedule().Returns(schedule ?? new DarkModeSchedule { Enabled = false });
        service.GetCurrentTheme().Returns(WindowsTheme.Light);
        service.SetTheme(Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        service.SaveSchedule(Arg.Any<DarkModeSchedule>()).Returns(true);
        return service;
    }

    [Fact]
    public void Constructor_Succeeds_WithCommands()
    {
        var vm = NewVm();
        Assert.NotNull(vm.SwitchToDarkCommand);
        Assert.NotNull(vm.SwitchToLightCommand);
    }

    [Fact]
    public void Constructor_SetsStatusMessage()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public void IsDarkNow_MatchesLiveWindowsTheme()
    {
        var vm = NewVm();
        // The VM seeds IsDarkNow from the live registry read; it must agree with a direct query.
        bool live = NewRealService().GetCurrentTheme() == WindowsTheme.Dark;
        Assert.Equal(live, vm.IsDarkNow);
    }

    [Fact]
    public void StatusMessage_ReflectsScheduleState()
    {
        var vm = NewVm();
        // The constructor picks the status text from ScheduleEnabled (loaded from disk).
        if (vm.ScheduleEnabled)
            Assert.Contains("Schedule is on", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains("schedule", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TimeStrings_AreNonEmpty_AfterLoad()
    {
        var vm = NewVm();
        // LoadFromSchedule populates the dark/light start strings (defaults if no saved file).
        Assert.False(string.IsNullOrWhiteSpace(vm.DarkStart));
        Assert.False(string.IsNullOrWhiteSpace(vm.LightStart));
    }

    // ── Mutating-path tests (substituted IWindowsThemeService) ─────────────

    [Fact]
    public async Task SwitchToDark_CallsSetThemeTrueOnce_AndReflectsState()
    {
        var service = FakeThemeService();
        var vm = NewVm(service);
        vm.ApplyToSystem = true;

        await vm.SwitchToDarkCommand.ExecuteAsync(null);

        // SetTheme(dark: true, includeSystem from ApplyToSystem) is the mutating call.
        service.Received(1).SetTheme(true, true);
        Assert.True(vm.IsDarkNow);
        Assert.Contains("dark", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchToLight_CallsSetThemeFalseOnce_AndReflectsState()
    {
        var service = FakeThemeService();
        var vm = NewVm(service);
        vm.ApplyToSystem = false;

        await vm.SwitchToLightCommand.ExecuteAsync(null);

        service.Received(1).SetTheme(false, false);
        Assert.False(vm.IsDarkNow);
        Assert.Contains("light", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchToDark_WhenServiceFails_SurfacesErrorAndLeavesStateUnchanged()
    {
        var service = FakeThemeService();
        service.SetTheme(Arg.Any<bool>(), Arg.Any<bool>()).Returns(false); // registry write denied
        var vm = NewVm(service);
        Assert.False(vm.IsDarkNow); // seeded from GetCurrentTheme() == Light

        await vm.SwitchToDarkCommand.ExecuteAsync(null);

        service.Received(1).SetTheme(true, Arg.Any<bool>());
        Assert.False(vm.IsDarkNow); // unchanged on failure
        Assert.Contains("Couldn't", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnablingSchedule_PersistsViaSaveSchedule_WithEnabledTrue()
    {
        // Start from a disabled schedule so toggling ScheduleEnabled is a real change.
        var service = FakeThemeService(new DarkModeSchedule { Enabled = false });
        var vm = NewVm(service);
        // Construction loads under _suppressSave, so no SaveSchedule yet.
        service.DidNotReceive().SaveSchedule(Arg.Any<DarkModeSchedule>());

        vm.ScheduleEnabled = true;
        // Saved once the schedule has been applied, which may switch the theme on a worker thread first.
        await vm.InitializationComplete;

        // OnScheduleEnabledChanged persists the new schedule with Enabled == true.
        service.Received().SaveSchedule(Arg.Is<DarkModeSchedule>(s => s != null && s.Enabled));
    }

    [Fact]
    public async Task EvaluateSchedule_WhileSuppressed_DoesNotWriteTheme()
    {
        // Regression (#1411): during LoadFromSchedule the [ObservableProperty] setters fire
        // one at a time under _suppressSave. Before the fix, _suppressSave gated only
        // SaveSchedule — NOT EvaluateSchedule — so the ScheduleEnabled setter fired
        // OnScheduleEnabledChanged -> EvaluateSchedule -> SetTheme while the other fields
        // still held their defaults, writing the wrong (possibly system-wide) theme against
        // the user's saved preference. The fix makes EvaluateScheduleAsync return early when
        // _suppressSave is set. This drives the exact condition FULLY deterministically (no
        // wall-clock dependency): DarkStart == LightStart makes ShouldBeDark return false
        // unconditionally, and IsDarkNow is forced to true, so an UNGUARDED EvaluateScheduleAsync
        // would see wantDark(false) != IsDarkNow(true) and call SetTheme. With the guard, the
        // suppressed evaluation must return before touching SetTheme.
        var service = FakeThemeService(new DarkModeSchedule { Enabled = false });
        var vm = NewVm(service);

        SetSuppressSave(vm, true);
        vm.DarkStart = "09:00";
        vm.LightStart = "09:00";   // equal → ShouldBeDark() is false regardless of the time
        vm.IsDarkNow = true;        // guaranteed mismatch vs wantDark=false
        service.ClearReceivedCalls();

        // A setter that calls EvaluateScheduleAsync while suppressed.
        vm.ScheduleEnabled = true;
        // Awaited, so a switch on a worker thread would have been made before the check below.
        await vm.InitializationComplete;

        service.DidNotReceive().SetTheme(Arg.Any<bool>(), Arg.Any<bool>());
    }

    // ── A schedule that cannot be saved (#2521) ─────────────

    [Fact]
    public void ChangingTheSchedule_WhenItCannotBeSaved_SaysSo()
    {
        var service = FakeThemeService();
        service.SaveSchedule(Arg.Any<DarkModeSchedule>()).Returns(false);
        var vm = NewVm(service);

        vm.ApplyToSystem = !vm.ApplyToSystem;

        Assert.Equal(ViewModelBase.ChangeNotSavedStatus, vm.StatusMessage);
    }

    [Fact]
    public async Task AScheduleThatCannotBeSaved_StaysOnTheStatusLine_WhenItAlsoSwitchesTheTheme()
    {
        // Equal times make ShouldBeDark false whatever the clock says, so turning the schedule on while Windows is
        // dark always switches it to light, and says so.
        var service = FakeThemeService(new DarkModeSchedule { Enabled = false, DarkStart = "09:00", LightStart = "09:00" });
        service.GetCurrentTheme().Returns(WindowsTheme.Dark);
        service.SaveSchedule(Arg.Any<DarkModeSchedule>()).Returns(false);
        var vm = NewVm(service);

        vm.ScheduleEnabled = true;
        await vm.InitializationComplete;

        service.Received(1).SetTheme(Arg.Is(false), Arg.Any<bool>());
        Assert.Equal(ViewModelBase.ChangeNotSavedStatus, vm.StatusMessage);
    }

    // ── The switch runs off the window's thread (#2750) ─────────────

    /// <summary>
    /// Pressing Dark or Light switches the theme off the window's thread, so a program that is slow to answer does
    /// not freeze SysManager while Windows tells it of the change (#2750).
    /// </summary>
    [Fact]
    public async Task SwitchingTheTheme_LeavesTheWindowFree_WhileOpenProgramsAreTold()
    {
        var service = FakeThemeService();
        var held = new HeldSwitch(service);
        var vm = NewVm(service);

        try
        {
            vm.SwitchToDarkCommand.Execute(null);

            Assert.False(held.HasReturned, "the switch held the thread that pressed the button until it was done");
        }
        finally
        {
            held.Release();
        }

        await Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.SwitchToDarkCommand).ExecutionTask!;
        service.Received(1).SetTheme(true, Arg.Any<bool>());
        Assert.True(vm.IsDarkNow);
        Assert.Equal("Switched to dark.", vm.StatusMessage);
    }

    /// <summary>A switch the schedule makes leaves the window free too, as the timer and the time boxes make it.</summary>
    [Fact]
    public async Task ASwitchTheScheduleMakes_LeavesTheWindowFree_WhileOpenProgramsAreTold()
    {
        // Equal times make ShouldBeDark false whatever the clock says, so turning the schedule on while Windows is
        // dark always switches it to light.
        var service = FakeThemeService(new DarkModeSchedule { Enabled = false, DarkStart = "09:00", LightStart = "09:00" });
        service.GetCurrentTheme().Returns(WindowsTheme.Dark);
        var held = new HeldSwitch(service);
        var vm = NewVm(service);

        try
        {
            vm.ScheduleEnabled = true;

            Assert.False(held.HasReturned, "the switch held the thread that turned the schedule on until it was done");
        }
        finally
        {
            held.Release();
        }

        await vm.InitializationComplete;
        service.Received(1).SetTheme(Arg.Is(false), Arg.Any<bool>());
        Assert.False(vm.IsDarkNow);
        Assert.Equal("Schedule applied light theme.", vm.StatusMessage);
    }

    /// <summary>
    /// While a switch runs, neither button can start another and the schedule does not either, so two switches never
    /// race to write the theme.
    /// </summary>
    [Fact]
    public async Task WhileASwitchRuns_NeitherTheButtonsNorTheScheduleStartAnother()
    {
        // Equal times make ShouldBeDark false whatever the clock says: with Windows dark, the schedule wants light.
        var service = FakeThemeService(new DarkModeSchedule { Enabled = false, DarkStart = "09:00", LightStart = "09:00" });
        service.GetCurrentTheme().Returns(WindowsTheme.Dark);
        var held = new HeldSwitch(service);
        var vm = NewVm(service);

        try
        {
            vm.SwitchToLightCommand.Execute(null);

            Assert.False(vm.SwitchToDarkCommand.CanExecute(null), "Dark could start a second switch");
            Assert.False(vm.SwitchToLightCommand.CanExecute(null), "Light could start a second switch");
            vm.ScheduleEnabled = true;
        }
        finally
        {
            held.Release();
        }

        await Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.SwitchToLightCommand).ExecutionTask!;
        await vm.InitializationComplete;
        service.Received(1).SetTheme(Arg.Any<bool>(), Arg.Any<bool>());
        Assert.True(vm.SwitchToDarkCommand.CanExecute(null));
        Assert.True(vm.SwitchToLightCommand.CanExecute(null));
    }

    /// <summary>
    /// A theme switch that waits, as one does while a busy program is slow to answer, and says whether it has returned.
    /// </summary>
    /// <remarks>
    /// SetTheme ends by telling every open window that the theme changed, and Windows gives each window up to five
    /// seconds to answer. Only the test releases it, and only once the thread that asked for the switch is free again,
    /// so code that switches on that thread would wait forever: the wait is bounded, and that code fails the test.
    /// </remarks>
    private sealed class HeldSwitch
    {
        private readonly TaskCompletionSource _released = new();
        private readonly TaskCompletionSource _returned = new();

        public HeldSwitch(IWindowsThemeService service) =>
            service.SetTheme(Arg.Any<bool>(), Arg.Any<bool>()).Returns(_ =>
            {
                _released.Task.Wait(TimeSpan.FromSeconds(10));
                _returned.TrySetResult();
                return true;
            });

        /// <summary>Whether the switch has returned.</summary>
        public bool HasReturned => _returned.Task.IsCompleted;

        public void Release() => _released.TrySetResult();
    }

    // ── A switch made here holds until the schedule's next change (#2760) ─────────────

    /// <summary>A clock that says what the test last set, in a time zone with no offset, so local time is that too.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTime Now { get; set; } = new(2026, 11, 2, 21, 0, 0);

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>A view model whose schedule is on, dark from 19:00 to 07:00, at <paramref name="clock"/>'s time.</summary>
    private static DarkModeViewModel Scheduled(IWindowsThemeService service, ManualClock clock)
    {
        service.LoadSchedule().Returns(new DarkModeSchedule { Enabled = true, DarkStart = "19:00", LightStart = "07:00" });
        var vm = new DarkModeViewModel(service, clock);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>What the schedule's timer does once a minute, run to its end.</summary>
    private static async Task Tick(DarkModeViewModel vm)
    {
        vm.OnScheduleTick();
        await vm.InitializationComplete;
    }

    /// <summary>
    /// Light pressed at 21:00, while the schedule wants dark, stays light at the next minute's check (#2760).
    /// </summary>
    /// <remarks>
    /// The check compared the theme the schedule wants with the one SysManager last set, so the switch it had just
    /// made was undone within a minute, while the same switch made in Windows Settings, which it does not see, held.
    /// </remarks>
    [Fact]
    public async Task ASwitchMadeHere_IsNotUndoneAtTheNextMinute()
    {
        var service = FakeThemeService();
        service.GetCurrentTheme().Returns(WindowsTheme.Dark);
        var clock = new ManualClock();
        var vm = Scheduled(service, clock);

        await vm.SwitchToLightCommand.ExecuteAsync(null);
        clock.Now = clock.Now.AddMinutes(1);
        await Tick(vm);

        service.DidNotReceive().SetTheme(Arg.Is(true), Arg.Any<bool>());
        Assert.False(vm.IsDarkNow);
        Assert.Equal("Switched to light. It stays so until the schedule turns dark at 19:00.", vm.StatusMessage);
    }

    /// <summary>
    /// Dark pressed at 10:00 holds through the light window, and the schedule takes over again at its next change.
    /// </summary>
    [Fact]
    public async Task ASwitchMadeHere_HoldsOnlyUntilTheSchedulesNextChange()
    {
        var service = FakeThemeService();
        var clock = new ManualClock { Now = new DateTime(2026, 11, 2, 10, 0, 0) };
        var vm = Scheduled(service, clock);

        await vm.SwitchToDarkCommand.ExecuteAsync(null);
        Assert.Equal("Switched to dark. It stays so until the schedule turns light at 07:00.", vm.StatusMessage);

        clock.Now = new DateTime(2026, 11, 2, 18, 59, 0);
        await Tick(vm);
        service.DidNotReceive().SetTheme(Arg.Is(false), Arg.Any<bool>());

        // 19:00 was the next change, and the schedule wants dark from then, so it is at 07:00 that it turns light.
        clock.Now = new DateTime(2026, 11, 3, 7, 0, 0);
        await Tick(vm);
        service.Received(1).SetTheme(Arg.Is(false), Arg.Any<bool>());
        Assert.False(vm.IsDarkNow);
    }

    [Theory]
    [InlineData("2026-11-02 21:00", "19:00", "07:00", "2026-11-03 07:00")]
    [InlineData("2026-11-02 10:00", "19:00", "07:00", "2026-11-02 19:00")]
    [InlineData("2026-11-02 19:00", "19:00", "07:00", "2026-11-03 07:00")]   // a change that is happening now is not next
    [InlineData("2026-11-02 06:59", "19:00", "07:00", "2026-11-02 07:00")]
    [InlineData("2026-11-02 08:00", "09:00", "09:00", "2026-11-02 09:00")]   // the same two times
    [InlineData("2026-11-02 09:00", "09:00", "09:00", "2026-11-03 09:00")]
    public void TheSchedulesNextChange_IsTheFirstOfItsTwoTimesStillToCome(
        string now, string darkStart, string lightStart, string expected)
    {
        static DateTime At(string value) =>
            DateTime.ParseExact(value, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        static TimeOnly Time(string value) =>
            TimeOnly.ParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(At(expected), DarkModeViewModel.NextChange(At(now), Time(darkStart), Time(lightStart)));
    }

    /// <summary>A change to the schedule is the newer word, so it ends a switch that was holding.</summary>
    [Fact]
    public async Task ChangingTheSchedule_EndsASwitchThatWasHolding()
    {
        var service = FakeThemeService();
        service.GetCurrentTheme().Returns(WindowsTheme.Dark);
        var clock = new ManualClock();
        var vm = Scheduled(service, clock);

        await vm.SwitchToLightCommand.ExecuteAsync(null);
        vm.LightStart = "06:30";
        await vm.InitializationComplete;

        service.Received(1).SetTheme(Arg.Is(true), Arg.Any<bool>());
        Assert.True(vm.IsDarkNow);
    }

    // ── A time the schedule cannot read is refused (#2760) ─────────────

    /// <summary>
    /// A time the schedule cannot read is refused: the box goes back to the time the schedule runs on, the status line
    /// says why, and nothing is saved (#2760).
    /// </summary>
    /// <remarks>
    /// The box saved whatever it was given, and the schedule read what it could not as 19:00 or 07:00 without a word,
    /// while the box went on showing the text.
    /// </remarks>
    [Theory]
    [InlineData("1900")]
    [InlineData("19h")]
    [InlineData("19;00")]
    [InlineData("")]
    public void ATimeTheScheduleCannotRead_IsRefused_AndTheBoxGoesBack(string typed)
    {
        var service = FakeThemeService(new DarkModeSchedule { Enabled = true, DarkStart = "21:00", LightStart = "07:00" });
        var vm = NewVm(service);
        service.ClearReceivedCalls();

        vm.DarkStart = typed;

        Assert.Equal("21:00", vm.DarkStart);
        Assert.Contains("still turns dark at 21:00", vm.StatusMessage, StringComparison.Ordinal);
        service.DidNotReceive().SaveSchedule(Arg.Any<DarkModeSchedule>());
    }

    /// <summary>A time it can read is written, and saved, the way it reads it.</summary>
    [Fact]
    public async Task ATimeTheScheduleReads_IsWrittenAndSavedAsItReadsIt()
    {
        var service = FakeThemeService(new DarkModeSchedule { Enabled = false });
        var vm = NewVm(service);

        vm.LightStart = "7:30";
        await vm.InitializationComplete;

        Assert.Equal("07:30", vm.LightStart);
        service.Received().SaveSchedule(Arg.Is<DarkModeSchedule>(s => s.LightStart == "07:30"));
        service.DidNotReceive().SaveSchedule(Arg.Is<DarkModeSchedule>(s => s.LightStart == "7:30"));
    }

    /// <summary>A saved time the schedule cannot read shows as the time it runs on, without a complaint at opening.</summary>
    [Fact]
    public void ASavedTimeTheScheduleCannotRead_ShowsAsTheTimeItRunsOn()
    {
        var vm = NewVm(FakeThemeService(new DarkModeSchedule { Enabled = true, DarkStart = "19h", LightStart = "7:00" }));

        Assert.Equal("19:00", vm.DarkStart);
        Assert.Equal("07:00", vm.LightStart);
        Assert.Equal("Schedule is on — the theme follows your set times while SysManager is running.", vm.StatusMessage);
    }

    private static void SetSuppressSave(DarkModeViewModel vm, bool value) =>
        typeof(DarkModeViewModel)
            .GetField("_suppressSave", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, value);
}
