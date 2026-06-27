// SysManager · DarkModeViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DarkModeViewModel"/>. <see cref="WindowsThemeService"/> is sealed
/// with no interface, so it cannot be substituted; these drive the real service but stay on
/// its read-only surface (<c>GetCurrentTheme</c> / <c>LoadSchedule</c>). The
/// <c>SwitchToDark</c> / <c>SwitchToLight</c> commands and any schedule-enabling mutation call
/// the real <c>SetTheme</c>, which writes HKCU and flips the actual Windows theme, so they are
/// NOT executed here. The pure schedule predicate <c>WindowsThemeService.ShouldBeDark</c> is
/// already covered by <see cref="WindowsThemeServiceTests"/> and is not duplicated.
///
/// With no <c>Application.Current</c> in the test host, the constructor skips the
/// DispatcherTimer/immediate-evaluate block, so building the VM does not apply the schedule.
/// </summary>
public class DarkModeViewModelTests
{
    private static DarkModeViewModel NewVm() => new(new WindowsThemeService());

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
}
