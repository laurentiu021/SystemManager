// SysManager · TimerResolutionViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="TimerResolutionViewModel"/>. Two layers of coverage:
/// the deterministic-surface tests drive the real <see cref="TimerResolutionService"/>
/// (its <c>Query</c> is a harmless read-only NT call) and assert construction, post-init
/// state, and the mutually-exclusive Enable/Disable CanExecute gating; the
/// mutating-path tests substitute <see cref="ITimerResolutionService"/> so the
/// Enable/Disable commands can be exercised without issuing a real
/// <c>NtSetTimerResolution</c> against this process — they assert the VM calls the
/// service exactly once and reflects the returned <see cref="TimerResolutionStatus"/>.
/// </summary>
public class TimerResolutionViewModelTests
{
    // The constructor kicks off an async RefreshAsync that reads the live timer state;
    // await it so the observable state is settled before asserting (mirrors the
    // DefenderViewModel / PrivacyViewModel idiom).
    private static TimerResolutionViewModel NewVm()
    {
        var vm = new TimerResolutionViewModel(new TimerResolutionService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    // A supported, default-resolution status: finest 0.5 ms, coarsest ~15.6 ms, current at
    // the coarse default → IsSupported, not high-res (so Enable is the available command).
    private static TimerResolutionStatus DefaultStatus()
        => new(FinestHundredNs: 5000, CoarsestHundredNs: 156250, CurrentHundredNs: 156250, EnabledByApp: false);

    // A supported, high-resolution status: current is at the finest → IsHighResolution.
    private static TimerResolutionStatus HighResStatus()
        => new(FinestHundredNs: 5000, CoarsestHundredNs: 156250, CurrentHundredNs: 5000, EnabledByApp: true);

    private static TimerResolutionViewModel NewVm(ITimerResolutionService service)
    {
        var vm = new TimerResolutionViewModel(service);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void Constructor_Succeeds_AndIsNotBusyAfterInit()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.EnableCommand);
        Assert.NotNull(vm.DisableCommand);
    }

    [Fact]
    public void Constructor_PopulatesStatusMessage()
    {
        var vm = NewVm();
        // RefreshAsync always sets a final status message (supported or not).
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public void AfterInit_DisplayProperties_AreSet()
    {
        var vm = NewVm();
        // Whether or not the API is available, the three display strings are non-empty:
        // either a formatted "x ms" value or the "—" placeholder.
        Assert.False(string.IsNullOrEmpty(vm.CurrentDisplay));
        Assert.False(string.IsNullOrEmpty(vm.FinestDisplay));
        Assert.False(string.IsNullOrEmpty(vm.CoarsestDisplay));
    }

    [Fact]
    public void EnableAndDisable_AreMutuallyExclusive_WhenSupported()
    {
        var vm = NewVm();
        // CanEnable and CanDisable are guarded so exactly one is available at a time on a
        // supported system (Enable requires not-high-res; Disable requires high-res).
        // The two can only be simultaneously false when the API is unsupported.
        if (vm.IsSupported)
        {
            Assert.NotEqual(vm.EnableCommand.CanExecute(null), vm.DisableCommand.CanExecute(null));
        }
        else
        {
            Assert.False(vm.EnableCommand.CanExecute(null));
            Assert.False(vm.DisableCommand.CanExecute(null));
        }
    }

    [Fact]
    public void Enable_DisabledOnUnsupportedSystem()
    {
        var vm = NewVm();
        // CanEnable == IsSupported && !IsHighResolution. When unsupported, never enabled.
        if (!vm.IsSupported)
            Assert.False(vm.EnableCommand.CanExecute(null));
    }

    [Fact]
    public void Disable_DisabledWhenNotHighResolution()
    {
        var vm = NewVm();
        // CanDisable == IsSupported && IsHighResolution. With no high-res request in effect
        // (the default for a freshly-constructed VM), Disable must be unavailable.
        if (!vm.IsHighResolution)
            Assert.False(vm.DisableCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshCommand_DoesNotThrow_AndKeepsStatus()
    {
        var vm = NewVm();
        // Refresh re-queries the live timer (read-only) — safe to execute repeatedly.
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.False(vm.IsBusy);
    }

    // ── Mutating-path tests (substituted ITimerResolutionService) ──────────

    [Fact]
    public async Task EnableCommand_CallsServiceEnableOnce_AndReflectsHighResStatus()
    {
        var service = Substitute.For<ITimerResolutionService>();
        // Construction's RefreshAsync sees the default (not high-res) state so Enable is gated on.
        service.Query().Returns(DefaultStatus());
        // The Enable command's outcome is the high-res status the service returns.
        service.Enable().Returns(HighResStatus());

        var vm = NewVm(service);
        Assert.True(vm.IsSupported);
        Assert.False(vm.IsHighResolution);
        Assert.True(vm.EnableCommand.CanExecute(null));

        await vm.EnableCommand.ExecuteAsync(null);

        service.Received(1).Enable();
        // The VM applied the returned status: it is now at high resolution.
        Assert.True(vm.IsHighResolution);
        Assert.Equal(HighResStatus().CurrentDisplay, vm.CurrentDisplay);
        Assert.Contains("enabled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // Enable→Disable are mutually exclusive: after a successful enable, Disable is the gate.
        Assert.False(vm.EnableCommand.CanExecute(null));
        Assert.True(vm.DisableCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisableCommand_CallsServiceDisableOnce_AndReflectsDefaultStatus()
    {
        var service = Substitute.For<ITimerResolutionService>();
        // Construction sees a high-res state so Disable is the gated-on command.
        service.Query().Returns(HighResStatus());
        // The Disable command returns the timer to the coarse default.
        service.Disable().Returns(DefaultStatus());

        var vm = NewVm(service);
        Assert.True(vm.IsHighResolution);
        Assert.True(vm.DisableCommand.CanExecute(null));

        await vm.DisableCommand.ExecuteAsync(null);

        service.Received(1).Disable();
        // The VM applied the returned status: no longer high resolution.
        Assert.False(vm.IsHighResolution);
        Assert.Equal(DefaultStatus().CurrentDisplay, vm.CurrentDisplay);
        Assert.Contains("default", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // Mutually exclusive again: after disable, Enable is back on and Disable is off.
        Assert.True(vm.EnableCommand.CanExecute(null));
        Assert.False(vm.DisableCommand.CanExecute(null));
    }
}
