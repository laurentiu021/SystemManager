// SysManager · TimerResolutionViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="TimerResolutionViewModel"/>. <see cref="TimerResolutionService"/>
/// is sealed with no interface, so it cannot be substituted; these drive the real service
/// (its <c>Query</c> is a harmless read-only NT call) and exercise the deterministic
/// surface only: construction, post-init state, and the mutually-exclusive Enable/Disable
/// CanExecute gating. The Enable/Disable commands themselves issue a real
/// <c>NtSetTimerResolution</c> against this process, so they are NOT executed here.
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
}
