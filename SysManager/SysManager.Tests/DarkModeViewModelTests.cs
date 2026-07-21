// SysManager · DarkModeViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
    private static DarkModeViewModel NewVm() => new(new WindowsThemeService());

    private static DarkModeViewModel NewVm(IWindowsThemeService service) => new(service);

    // A substitute that loads a known schedule (so LoadFromSchedule has a non-null result)
    // and reports the Windows theme as light by default. SetTheme succeeds unless overridden.
    private static IWindowsThemeService FakeThemeService(DarkModeSchedule? schedule = null)
    {
        var service = Substitute.For<IWindowsThemeService>();
        service.LoadSchedule().Returns(schedule ?? new DarkModeSchedule { Enabled = false });
        service.GetCurrentTheme().Returns(WindowsTheme.Light);
        service.SetTheme(Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
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
        bool live = new WindowsThemeService().GetCurrentTheme() == WindowsTheme.Dark;
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
    public void SwitchToDark_CallsSetThemeTrueOnce_AndReflectsState()
    {
        var service = FakeThemeService();
        var vm = NewVm(service);
        vm.ApplyToSystem = true;

        vm.SwitchToDarkCommand.Execute(null);

        // SetTheme(dark: true, includeSystem from ApplyToSystem) is the mutating call.
        service.Received(1).SetTheme(true, true);
        Assert.True(vm.IsDarkNow);
        Assert.Contains("dark", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchToLight_CallsSetThemeFalseOnce_AndReflectsState()
    {
        var service = FakeThemeService();
        var vm = NewVm(service);
        vm.ApplyToSystem = false;

        vm.SwitchToLightCommand.Execute(null);

        service.Received(1).SetTheme(false, false);
        Assert.False(vm.IsDarkNow);
        Assert.Contains("light", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchToDark_WhenServiceFails_SurfacesErrorAndLeavesStateUnchanged()
    {
        var service = FakeThemeService();
        service.SetTheme(Arg.Any<bool>(), Arg.Any<bool>()).Returns(false); // registry write denied
        var vm = NewVm(service);
        Assert.False(vm.IsDarkNow); // seeded from GetCurrentTheme() == Light

        vm.SwitchToDarkCommand.Execute(null);

        service.Received(1).SetTheme(true, Arg.Any<bool>());
        Assert.False(vm.IsDarkNow); // unchanged on failure
        Assert.Contains("Couldn't", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnablingSchedule_PersistsViaSaveSchedule_WithEnabledTrue()
    {
        // Start from a disabled schedule so toggling ScheduleEnabled is a real change.
        var service = FakeThemeService(new DarkModeSchedule { Enabled = false });
        var vm = NewVm(service);
        // Construction loads under _suppressSave, so no SaveSchedule yet.
        service.DidNotReceive().SaveSchedule(Arg.Any<DarkModeSchedule>());

        vm.ScheduleEnabled = true;

        // OnScheduleEnabledChanged persists the new schedule with Enabled == true.
        service.Received().SaveSchedule(Arg.Is<DarkModeSchedule>(s => s != null && s.Enabled));
    }

    [Fact]
    public void EvaluateSchedule_WhileSuppressed_DoesNotWriteTheme()
    {
        // Regression (P2 #45): during LoadFromSchedule the [ObservableProperty] setters fire
        // one at a time under _suppressSave. Before the fix, _suppressSave gated only
        // SaveSchedule — NOT EvaluateSchedule — so the ScheduleEnabled setter fired
        // OnScheduleEnabledChanged -> EvaluateSchedule -> SetTheme while the other fields
        // still held their defaults, writing the wrong (possibly system-wide) theme against
        // the user's saved preference. The fix makes EvaluateSchedule return early when
        // _suppressSave is set. This drives the exact condition FULLY deterministically (no
        // wall-clock dependency): DarkStart == LightStart makes ShouldBeDark return false
        // unconditionally, and IsDarkNow is forced to true, so an UNGUARDED EvaluateSchedule
        // would see wantDark(false) != IsDarkNow(true) and call SetTheme. With the guard, the
        // suppressed evaluation must return before touching SetTheme.
        var service = FakeThemeService(new DarkModeSchedule { Enabled = false });
        var vm = NewVm(service);

        SetSuppressSave(vm, true);
        vm.DarkStart = "09:00";
        vm.LightStart = "09:00";   // equal → ShouldBeDark() is false regardless of the time
        vm.IsDarkNow = true;        // guaranteed mismatch vs wantDark=false
        service.ClearReceivedCalls();

        // A setter that calls EvaluateSchedule while suppressed.
        vm.ScheduleEnabled = true;

        service.DidNotReceive().SetTheme(Arg.Any<bool>(), Arg.Any<bool>());
    }

    private static void SetSuppressSave(DarkModeViewModel vm, bool value) =>
        typeof(DarkModeViewModel)
            .GetField("_suppressSave", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, value);
}
