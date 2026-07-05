// SysManager · DebloaterViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DebloaterViewModel"/> empty-state copy — the centered empty
/// state must switch from "press Refresh" (never scanned) to "none found" (scanned,
/// zero results) so it never contradicts the status bar. Constructed with a mocked
/// <see cref="IPowerShellRunner"/> so no real PowerShell runs.
/// </summary>
public class DebloaterViewModelTests
{
    private static DebloaterViewModel NewVm() =>
        new(new DebloaterService(Substitute.For<IPowerShellRunner>()));

    [Fact]
    public void EmptyState_BeforeScan_PromptsRefresh()
    {
        var vm = NewVm();
        Assert.False(vm.HasScanned);
        Assert.Equal("No apps loaded", vm.EmptyTitle);
        Assert.Contains("Refresh", vm.EmptyMessage);
    }

    [Fact]
    public void EmptyState_AfterScan_SwitchesToNoneFound()
    {
        var vm = NewVm();

        // Simulate a completed scan (the flag the RefreshAsync path sets on completion).
        vm.HasScanned = true;

        Assert.Equal("No Store apps found", vm.EmptyTitle);
        Assert.DoesNotContain("Refresh", vm.EmptyMessage);
    }
}
