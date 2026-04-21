using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class SystemHealthViewModelTests
{
    [Fact]
    public void Ctor_DefaultsAreSafe()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        Assert.Empty(vm.Modules);
        Assert.Empty(vm.Disks);
        Assert.Null(vm.Os);
        Assert.Null(vm.Cpu);
        Assert.Null(vm.Memory);
        Assert.False(string.IsNullOrWhiteSpace(vm.Summary));
    }

    [Fact]
    public async Task ScanCommand_PopulatesInfo()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Os);
        Assert.NotNull(vm.Cpu);
        Assert.NotNull(vm.Memory);
        Assert.False(string.IsNullOrWhiteSpace(vm.Summary));
    }

    [Fact]
    public async Task ScanCommand_ResetsBusy()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ScanCommand_IsIdempotent()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        await vm.ScanCommand.ExecuteAsync(null);
        var firstDiskCount = vm.Disks.Count;
        await vm.ScanCommand.ExecuteAsync(null);
        // Rescan replaces; count remains consistent with the hardware.
        Assert.Equal(firstDiskCount, vm.Disks.Count);
    }
}
