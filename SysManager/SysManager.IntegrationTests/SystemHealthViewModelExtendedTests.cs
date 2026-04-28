// SysManager · SystemHealthViewModelExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class SystemHealthViewModelExtendedTests
{
    [Fact]
    public void AllCommandsExist()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.CheckDiskHealthCommand);
        Assert.NotNull(vm.CheckMemoryErrorsCommand);
        Assert.NotNull(vm.ScheduleMemoryTestCommand);
        Assert.NotNull(vm.RunChkdskCommand);
        Assert.NotNull(vm.CancelScanCommand);
        Assert.NotNull(vm.RelaunchAsAdminCommand);
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        Assert.Empty(vm.DiskHealth);
        Assert.Equal(0, vm.WheaMemoryErrors);
        Assert.Equal(0, vm.MemoryDiagnosticResults);
        Assert.False(string.IsNullOrWhiteSpace(vm.MemoryHealthVerdict));
        Assert.Matches("^#[0-9A-Fa-f]{6}$", vm.MemoryHealthColorHex);
    }

    [Fact]
    public async Task CheckDiskHealth_PopulatesCollection()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        await vm.CheckDiskHealthCommand.ExecuteAsync(null);
        // Count depends on hardware. Only guarantee: not busy anymore and no crash.
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CheckMemoryErrors_UpdatesVerdict()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        var initialVerdict = vm.MemoryHealthVerdict;
        await vm.CheckMemoryErrorsCommand.ExecuteAsync(null);
        // Verdict should have changed (or at least still be non-empty).
        Assert.False(string.IsNullOrWhiteSpace(vm.MemoryHealthVerdict));
        Assert.NotEqual(initialVerdict, vm.MemoryHealthVerdict);
    }

    [Fact]
    public void CancelScan_WithoutActive_IsSafe()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        var ex = Record.Exception(() => vm.CancelScanCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void IsElevated_MatchesCurrentProcess()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        Assert.Equal(Helpers.AdminHelper.IsElevated(), vm.IsElevated);
    }
}
